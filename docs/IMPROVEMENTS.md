# 改进评审与落实记录（2026-09）

对 `master` @ `bc99ce8` 的一次工程评审，以及随后逐项的落实情况。按 P0（正确性/安全）、
P1（可维护性/工程治理）、P2（细节）排列，每项给出**落点文件**，便于复核。

## P0 — 正确性与安全

| # | 问题 | 落实 | 落点 |
|---|---|---|---|
| P0-1 | **序号生成存在并发竞态，会产重号。** `SerialNumberGenerator` 的 `SemaphoreSlim` 是实例字段、服务注册为 Scoped，而 `CollectSessionCoordinator.PersistAsync` 每次落库都 `CreateAsyncScope()`；叠加 `CollectionHostedService` 用 `Task.Run` 并行跑各工站 → 互斥范围为零，"读 5 写 6"丢更新 | 自增改由数据库原子完成：单条 `INSERT ... ON CONFLICT("DayKey") DO UPDATE SET "LastValue"="LastValue"+1 RETURNING "LastValue"`，并叠一层乐观重试；服务改为**单例**（已无状态）；并发测试强化为"24 路调用正好覆盖 1..24" | `src/DataTrace.Infrastructure/Persistence/SerialNumberGenerator.cs`、`InfrastructureServiceCollectionExtensions.cs`、`tests/DataTrace.Tests/PersistenceStoreTests.cs` |
| P0-2a | **生产种入源码里写死的口令**（`admin/Admin@123` 等 4 个账号，`DatabaseSeeder` 无环境门） | 生产**一律**不种演示账号，且没有开关能把它打开；其它环境默认只 Development 种，可用 `Seed:DemoUsers` 显式开启（E2E 的鉴权夹具跑在 Staging，需要它）。生产首启动只建 `admin`，口令**随机生成**并只打印一次到日志，置 `MustChangePassword`；可用 `Seed:AdminPassword` 指定。升级上来的老库若 `admin` 仍在用演示口令，启动时自动补强制改密标记 | `src/DataTrace.Infrastructure/Seeding/SeedUserOptions.cs`、`DatabaseSeeder*.cs`、`src/DataTrace.Web/Program.cs` |
| P0-2b | **强制改密链路** | `ApplicationUser.MustChangePassword` + 迁移补列；`ApplicationUserClaimsPrincipalFactory` 把标记写进登录主体；管道拦下未改密用户；新增 `/change-password` 页与 `/account/change-password` 端点（表单 POST，改完重发 Cookie）；自助改密时原子清标记 | `Identity/ApplicationUser.cs`、`ApplicationUserClaimsPrincipalFactory.cs`、`UserAdministration.cs`、`Components/Pages/ChangePassword.razor`、`Program.cs` |
| P0-2c | **全链路无传输加密** | 配了 `Kestrel:Endpoints:Https` 即自动把会话 Cookie 钉 `Secure`（原默认 `SameAsRequest` 会在一次 http 访问时明文发出凭据）、非开发态下发 HSTS。代码不改，仅配置 | `src/DataTrace.Web/Program.cs`、`README.md`、`deploy/CHECKLIST.md` |
| P0-3 | **无健康检查端点** | 新增 `GET /healthz`（不健康返回 503）：配置库可读、数据盘可写、采集器心跳新鲜；匿名可访问，只回结论与错误摘要，不回显安装路径 | `src/DataTrace.Web/Services/HealthProbe.cs`、`Program.cs` |

## P1 — 可维护性与工程治理

| # | 问题 | 落实 | 落点 |
|---|---|---|---|
| P1-1 | **巨型类** | 按职责拆成 `partial` 分文件（`RuntimeStore` 1210 → 237+239+740+30；`ConfigRepository` 1129 → 6 份；`DatabaseBackupService` 671 → 3 份；`DatabaseSeeder` 634 → 4 份；`ConfigurationCommands` 587 → 5 份；`RuntimeStatusHub` 568 → 4 份）。修饰符与基接口只写在主文件那一份 | 各 `*.cs` 与同目录 `*.{Queries,Analytics,Retention,Plc,Stations,Curves,Recipes,Settings,Files,Schema,Maintenance,DemoLine,Streaks,Sessions,Equality}.cs` |
| P1-2 | **无集中包管理（CPM）** | 新增 `Directory.Packages.props`，9 个 csproj 里的 `Version=` 全部移除（30 处） | `Directory.Packages.props` |
| P1-3 | **`Microsoft.Extensions.*` 版本漂移（8.0.1 / 8.0.2 混用）** | 每个包取**其 8.0.x 线的最高补丁**（实测该家族收尾版本并不一致：Abstractions 系到 8.0.2/8.0.3，Hosting/Http/Logging.Debug/WindowsServices 止于 8.0.1），并在文件里写清口径 | `Directory.Packages.props` |
| P1-4 | **无 `global.json`，构建不可复现**（CI 装 8.0.x + 10.0.x，SDK 选择不确定） | 新增 `global.json` 固定 SDK 10.0.100（`latestFeature`，slnx 需要它）；CI 步骤名与注释同步说明"构建用 10.0.x、跑测试用 8.0.x" | `global.json`、`.github/workflows/dotnet-test.yml` |
| P1-5 | **CI 关闭了像素视觉回归**（`DATATRACE_E2E_GOLDEN=off`） | 新增 `auto` 档并用作 CI 默认：基线存在**且录制环境指纹（OS + 浏览器版本）一致**才真比对，否则退化为"验截得出图"。`update` 时写下 `golden/environment.json`；在 runner 上录一次基线即自动升级为真比对 | `tests/DataTrace.E2E.Tests/VisualGolden.cs`、`VisualRegressionE2ETests.cs`、`.github/workflows/dotnet-test.yml` |
| P1-6 | **单测以源码链接引用 Web 层纯函数**（脆弱） | 新增 `DataTrace.Shared` 工程承载 CSV 导出、图表几何、语义色、概念文案、返回地址校验等纯函数；测试改为工程引用，删掉 7 条 `<Compile Include>` 链接 | `src/DataTrace.Shared/`、`DataTrace.slnx`、各测试 csproj |

## P2 — 细节与打磨

| # | 问题 | 落实 | 落点 |
|---|---|---|---|
| 1 | `.AddInteractiveServerRenderMode()` 重复调用两次 | 去掉重复的一次，并补上 `DisableAntiforgery` 的取舍说明 | `src/DataTrace.Web/Program.cs` |
| 2 | 悬空/截断注释（"Soft-404：…"半句） | 补全并移到它真正描述的 `MapRazorComponents` 上方 | `src/DataTrace.Web/Program.cs` |
| 3 | `PRAGMA table_info({table})` 等字符串插值拼标识符 | 加 `RequireIdentifier` 白名单校验（字母/数字/下划线、不以数字开头），并统一加引号 | `src/DataTrace.Infrastructure/Persistence/SqliteSchema.cs` |
| 4 | 审计导出临时文件不清理 | 改为 `FileOptions.DeleteOnClose`（响应写完即消失），并顺手清扫超过一天的历史残留 | `src/DataTrace.Web/Program.cs` |
| 5 | 无 `.editorconfig`；未开文档生成 | 新增 `.editorconfig`（只声明约定，不把样式规则提成 error，避免与"零警告"门禁打架）。**未启用 `GenerateDocumentationFile`**：本项目是应用而非对外库，开启后会把既有 XML 注释里的引用错误变成编译错误，收益不抵风险，留待单独评估 | `.editorconfig` |
| 6 | Windows 专有实现的平台耦合 | README 增加「平台说明」列出需要替换的三处（文件选择对话框、Windows 服务宿主、E2E 像素比对的 `System.Drawing`） | `README.md` |
| 7 | 开发态自动登录 admin 的误用风险 | 生产引导口令改为随机 + 强制改密后风险已收敛；登录页提示同步改为"演示账号只存在于开发环境"，`deploy/CHECKLIST.md` 增加口令与传输安全的前置条件 | `Components/Pages/Login.razor`、`deploy/CHECKLIST.md` |

## 验证

全部在 `global.json` 所钉的 **.NET 10.0.401 SDK** 下执行（`rollForward: latestFeature` 正确解析）。

| 项 | 结果 |
|---|---|
| `dotnet build DataTrace.slnx -c Release` | **0 警告 0 错误** |
| `tests/DataTrace.Tests` | **1271 项全部通过**（含新增的序号并发用例；该用例单独重复执行 15 次均通过） |
| `tests/DataTrace.Web.Tests` | **237 项全部通过** |
| `tests/DataTrace.E2E.Tests`（`DATATRACE_E2E_GOLDEN=auto`） | **96 项全部通过** |
| 真实启动冒烟（Development） | `/healthz` 四项全绿（config-db / data-root / runtime-dir / collector） |
| 真实启动冒烟（Production） | 随机口令生成 → 未种任何演示账号 → 使用该口令登录 302 到 `/change-password` → 访问 `/`、`/query`、`/config/settings` 均被拦 → `/app.css`、`/favicon.png`、`/manifest.webmanifest`、`/js/*`、`/_content/*` 均 200（否则改密页会没样式）→ `/healthz` 200 → 改密后 `/` 返 200 |

两点如实说明：

- 用 **.NET 8 SDK** 构建 `tests/DataTrace.Web.Tests` 会报 15 个 `CS0854`（表达式树含可选参数）与
  `CS1998`（异步缺 await）。这些在改动前的 `bc99ce8` 上**同样存在**（已用独立 worktree 逐项比对），
  属 SDK/语言版本差异，**不是本次改动引入的**；用 `global.json` 所钉的 .NET 10 SDK 构建即通过。
- `DataTrace.Tests` 曾出现过**一次**无法复现的失败：紧接着一轮 E2E（整机负载较高）之后那次运行
  报 1 项失败，随后 5 次全量与 15 次定向重跑均通过，未能定位到具体用例。若 CI 偶发红灯，
  先看是不是同一个时序敏感的采集类用例。

## 有意保留（未改）

- **EF Core / Identity 停在 8.0.11**：持久化层依赖 SQLite 的 PRAGMA、WAL 与 `RETURNING` 行为，
  升到 8.0.x 线尾（8.0.31）应当单独开一次变更并跑分库/备份/恢复回归。已在
  `Directory.Packages.props` 里写明。
- **审计导出仍是"先落临时文件再发"**：改法是修好了生命周期，而不是重构成流式导出；
  单次导出量不大，不值得为它牺牲"导出脚本可断点重试"这个特性。

## 追加一轮：可观测性三项（2026-09）

评审后另起的一轮，主题都是"故障发生时现场拿不到东西"。三项要么现在就会出事，
要么是后面所有排查工作的地基。

| # | 问题 | 落实 | 落点 |
|---|---|---|---|
| A | **日志无体积上限**：`WriteTo.File` 只按天滚动，没有 `fileSizeLimitBytes` / `retainedFileCountLimit`。PLC 断链重连风暴或一次异常刷屏能在单日写出 GB 级日志，把数据盘写满 —— 而数据盘一满 SQLite 开始落库失败，现场看到的是"采集莫名停了" | 加上单文件上限 + `rollOnFileSizeLimit` + 封顶份数（默认 32 MB × 30 ≈ 960 MB），两个值可由 `Logging:File:FileSizeLimitMb` / `Logging:File:RetainedFileCount` 覆盖。**配置读坏（0/负数/写错单位）时回默认值，不退化回"没有上限"** | `src/DataTrace.Web/Program.cs` |
| B | **磁盘写满 = 静默停库**：`HealthProbe` 只测目录可写，不看还剩多少 | 新增 `disk-free` 检查项（低于 `SystemDefaults.MinDiskFreeMegabytes` 判不健康；读不到剩余空间同样判不健康）。报警侧新增 `LineAlarmKind.DiskLow`，**刻意不受 `collectEnabled` 约束**（检修停机时备份与归档照样吃盘）；`null` 不叫 —— "不知道"归探活报，"确实不够"才响铃 | `Services/HealthProbe.cs`、`src/DataTrace.Shared/DiskSpace.cs`、`Application/Alarms/LineAlarm{,Rules}.cs`、`Web/Services/LineAlarmHostedService.cs` |
| C | **异常无法回报**：页面只有一块"系统发生错误"，日志里有堆栈，两边接不上头 | 未处理异常在中间件里配一个 8 位回报码（`ErrorTrace`），同时写进日志与 `HttpContext.Items`，`/Error` 页把它显示出来。中间件挂在异常处理器**内层**并原样抛回异常，所以 HTTP 仍是 500；`IsWellFormed` 校验形状，避免错误页（匿名可达）变成反射任意文本的出口 | `Web/Services/ExceptionTraceMiddleware.cs`、`src/DataTrace.Shared/ErrorTrace.cs`、`Components/Pages/Error.razor` |

### 验证

| 项 | 结果 |
|---|---|
| `dotnet build DataTrace.slnx -c Release` | **0 警告 0 错误** |
| `tests/DataTrace.Tests` | **1290 项通过**（新增 19 项：`ErrorTrace`/`DiskSpace`/磁盘报警） |
| `tests/DataTrace.Web.Tests` | **243 项通过**（新增 6 项：中间件 3 + 错误页 3） |
| 体积闸门实测 | 把上限调成 1 MB、保留 2 份后打 800 次异常：文件在 1,048,577 字节处滚成 `_003`/`_004`，旧份被删，目录总量 1.3 MB |
| 回报码实测（Production 真实启动） | 请求一个抛异常的端点 → 响应 500 且错误页渲染出 `dt-error-code">0C8BDFAC`；日志同一行 `未处理异常（回报码 0C8BDFAC）：GET /__trace-probe`，**页面上那串与日志里那串一致**（探针端点验证后已删除） |

一点如实说明：`PlcRequestQueueTests.Queue_failsFast_whileCoolingDownAfterLinkLoss` 是**既有的时序敏感用例**
（`IsCoolingDown` 只是个 5 秒窗口，机器负载高时会过期）。本轮在 `HEAD`（`a19f8f2`）的独立工作树上
复跑同一套件，**该用例同样失败过一次**，与本轮改动无关。

## 追加一轮：采集侧可观测与托盘时序（2026-09）

接着上面三项往下做：把"采集进程这一会儿在干什么"摊开，并把托盘追溯补成一条带间隔的完整时序。

| # | 问题 | 落实 | 落点 |
|---|---|---|---|
| D | **PLC 通信没有流水可看**：`PlcRuntimeStatus` 只有 `Connected` + 一个 `LastError` 字符串，点位读不到值只能猜 | `PlcRequestQueue` 是所有 PLC 请求的唯一收口，在这里逐条记录读/写的地址、字数、耗时、成败、错误与前几个字的值（写请求也记写下去的值）。固定容量环形缓冲（默认 40 条），**不提供开关** —— 需要配置才能打开的观测手段，出事时多半是关着的 | `src/DataTrace.Plc/Queue/PlcTraffic.cs`、`PlcRequestQueue.cs` |
| E | **采集循环没有耗时度量**：无法回答"节拍为什么掉了" | 主循环每一轮记录工作耗时、扫描耗时、心跳耗时、PLC 数、触发数、冷却跳过的 PLC、被跳过的工站；超时轮次单独计数。放在 `finally` 里 —— 抛异常的那一轮最需要被看见。采集关闭/未读到配置的轮次不记，否则会把均值拉低 | `DataTrace.Collector/CollectionHostedService.cs` |
| F | **回写重试与补传全不可见**：`WriteRetry` 可配但界面看不见；spool 只暴露一个件数 | 回写改为返回尝试次数与最终结果，进 `StationRuntimeStatus`（工站卡片只在"重试 ≥2 或失败"时显示，常态不占位）。补传失败时写旁车文件记录尝试次数与原因，`ISpoolStore` 新增 `ListEntriesAsync` / `NoteFailureAsync`；`SpoolReplayRunner` 让后台循环与手动补传共用一把锁，避免同一条被写两遍 | `StationCollectPipeline.cs`、`Storage/FileSpoolStore.cs`、`Collector/SpoolReplayRunner.cs`、`RuntimeContracts.cs` |
| G | **托盘缺全链路时序**：已有工站履历，但看不出时间花在哪一段 | 履历补上"本站触发距上一站完成"的间隔、本站采集耗时、全链路总时长，并在末尾接上会话结束与 MES 推送两节（推没推、试了几次、上次为什么没成）。间隔算的是实际发生的那一段，缺站时会如实变长 | `Shared/ProductTraceabilityPanel.razor`、`DataTrace.Shared/PalletTimeline.cs`、`IConfigRepository.FindMesOutboxAsync` |
| H | **新增 `/diagnostics` 运行诊断页**（管理员/工程师） | 三块：采集循环、PLC 通信（每台可展开流水）、补传队列（立即补传 / 丢弃，丢弃走二次确认）。拉模式，不订阅推送 —— PLC 请求每轮成百上千次，接进 hub 会让看板每 75 毫秒重画一遍。补传明细超过 200 条时不再自动列，改由用户显式要一次 | `Components/Pages/Diagnostics.razor`、`Application/Realtime/DiagnosticsContracts.cs`、`Collector/CollectorDiagnostics.cs` |

### 一处行为修正（不是回归）

`FileSpoolStore.ListAsync` 原来遇到坏掉的缓存文件会**整体抛异常**，于是补传循环每轮都在同一个文件上失败，
排在它后面的件永远轮不到。现在坏文件被跳过（队列照常往前走），同时它在明细里如实出现、并仍计入积压数。
`StorageTests.Corrupted_entry_surfaces_error_from_list` 断言的是这条旧行为，已改写为
`Corrupted_entry_is_skipped_but_still_visible`。

### 一处测试修正（消除既有 flake）

`Queue_failsFast_whileCoolingDownAfterLinkLoss` 用真墙钟断言 5 秒冷却窗口，而断言要等本用例的续体被
线程池调度起来才执行 —— 整机满载时这段调度延迟能超过窗口，于是负载高时偶发变红。
`PlcRequestQueue` 新增可注入单调时钟（默认仍是 `Environment.TickCount64`，生产行为不变），
用例改用手动时钟并补一条"冷却过期后重新连接"的确定性用例。

### 验证

| 项 | 结果 |
|---|---|
| `dotnet build DataTrace.slnx -c Release` | **0 警告 0 错误** |
| `tests/DataTrace.Tests` | **1332 项全部通过**（本轮新增 42 项；此前偶发红的 PLC 用例已确定性化） |
| `tests/DataTrace.Web.Tests` | **248 项全部通过**（新增 5 项诊断页用例） |
| `tests/DataTrace.E2E.Tests`（`DATATRACE_E2E_GOLDEN=auto`） | **101 项全部通过**（新增 `/diagnostics` 路由、可访问名普查、几何断言、视觉截图四条，以及一条"真数据下托盘时序渲染出间隔与终点"的用例） |

## 追加一轮：采集链路关联（2026-09）

一次采集跨两个后台循环、四个类，日志里却没有共同字段。现场说"某件丢了"时，
写库失败那行只有工站码、补传那行只有缓存文件名，而流水号只活在记录对象里 —— 只能靠时间戳手工比对。

| # | 问题 | 落实 | 落点 |
|---|---|---|---|
| I | **日志级别配置从来没生效**：`UseSerilog(ReadFrom.Configuration)` 只读 `Serilog` 段，而 appsettings 里只有给 Microsoft.Extensions.Logging 用的 `Logging:LogLevel`。实测：那里配着 `Microsoft.EntityFrameworkCore: Warning`，日志文件里照样躺着上千条 SQL | 级别搬进 `Serilog:MinimumLevel` 段（删掉失效的 `Logging` 段），并按 `{Properties}` 补上输出模板；控制台与文件共用同一份模板 | `appsettings*.json`、`Program.cs` |
| J | **一次采集的日志没有身份**：读取、判定、落库、故障、回写散在四个类里，各记各的，同一工站连续两件分不清哪行属于哪一件 | 流水线在 `ExecuteAsync` 开外层作用域（工站 + 触发时刻），协调器在流水号定下来后开内层作用域（流水号）；两层叠加渲染。作用域走 `AsyncLocal`，各工站并发采集互不串味 | `Collector/CollectionLogScope.cs`、`StationCollectPipeline.cs`、`CollectSessionCoordinator.cs` |
| K | **补传那一段彻底对不上**：写库失败只记工站码，补传成功/失败只记缓存文件名 —— 两端没有共同字段 | `ISpoolStore.SaveAsync` 改为返回缓存文件名并在"已转存本地缓存"那行记出来；`SpoolReplayRunner` 的补传成功/失败改为同时记文件名与流水号 | `RuntimeContracts.cs`、`FileSpoolStore.cs`、`SpoolReplayRunner.cs` |
| L | **没有可搜的锚点**：正常件没有任何一行能拿流水号搜到 | 每件记一行 `采集完成 {Serial} 结果 {Result} 判定 {Judgement} 耗时 {DurationMs}ms 回写 {WriteBack}`。正常件也记 —— 只记异常的话，来查正常件时会发现根本没有锚点可搜 | `StationCollectPipeline.cs` |

**为什么每件一行不算噪音**：这一轮同时消掉了每轮好几条 EF Core 的 SQL 日志。
实测同一时段：原先日志文件里有 **1061 条 `Executed DbCommand`**，现在 **0 条**，总量反而从上千行降到几十行。

### 验证（真实启动，开发态，15 秒窗口）

```
[INF]{ SourceContext: "DataTrace.Collector.StationCollectPipeline", CollectStation: "ST010", CollectTrigger: "16:10:53.837" } 采集完成 20260930-000073 结果 采集成功 判定 "Ok" 耗时 450ms 回写 1 次成功
[INF]{ ... CollectStation: "ST020", CollectTrigger: "16:10:54.846" } 采集完成 20260930-000073 结果 采集成功 判定 "Ok" 耗时 76ms 回写 1 次成功
[INF]{ ... CollectStation: "ST030", CollectTrigger: "16:10:55.454" } 采集完成 20260930-000073 结果 采集成功 判定 "Ok" 耗时 41ms 回写 1 次成功
```

- `Executed DbCommand` 行数：**0**（改前同一时段 1061）——级别配置这次真的生效了
- 作用域属性已渲染：`CollectStation` / `CollectTrigger` 逐行可见
- **同一个流水号 `20260930-000073` 在三个工站各有一行**：按流水号一搜就是整条产线路径，
  这正是这一轮要达到的效果

| 项 | 结果 |
|---|---|
| `dotnet build DataTrace.slnx -c Release` | **0 警告 0 错误** |
| `tests/DataTrace.Tests` | **1335 项全部通过**（新增 3 项：写库失败日志带身份、作用域带工站与触发时刻、锚点行） |
| `tests/DataTrace.Web.Tests` | **248 项全部通过** |

## 追加一轮：采集循环的耗时度量与调度口径（2026-09）

实跑时诊断页报"平均 200 ms / 配置 200 ms / 28 轮超时"，看着像采集循环已经贴着扫描间隔跑满。
同一页上扫描与心跳却都是 **0 ms** —— 顺着这两组数字查下去，发现是**两个各自独立的问题**。

| # | 问题 | 落实 | 落点 |
|---|---|---|---|
| M | **埋点把"等待"算进了工作量**：读数写在 `finally` 里，而 `finally` 跑在 `Task.Delay(间隔)` 之后。于是每轮"工作耗时"恒等于扫描间隔，"超时"永远在报 —— 这一页最核心的两个数因此全是假的 | 在进入等待**之前**先取读数（异常轮次由 `catch` 在 1 秒重试等待之前补记），值用 `long?` 表示"还没记"。现在读数只含干活的部分 | `Collector/CollectionHostedService.cs` |
| N | **调度是"干完再等"，与文档口径不符**：`HelpTexts.ScanInterval` 写的是"轮询 PLC 的**周期**"，而实现是每轮干完活再整个等一个间隔，实际周期 = 工作 + 间隔 —— 工作量一涨，采样就悄悄变慢，而设置页上的数字还写着原值 | 改成按周期调度：扣掉这一轮已花掉的时间再等（`interval - work`），并设 1 ms 的下限避免紧密循环；间隔下限取 `SettingsLimits.MinScanIntervalMs`（顺带替掉原先写死的 `Math.Max(20, …)`） | 同上 |

**为什么第 N 条值得改**：工作耗时才 0–2 ms 时，两种算法只差 1%，看不出区别；
但在**小间隔**下差别很大 —— 间隔 20 ms（下限）、工作 15 ms 时，实际周期 35 ms vs 20 ms，差了 75%。
而设小间隔的人要的正是低延迟。

### 验证（真实启动，开发态，PLC 1 台 / 工站 6 个 / 间隔 200 ms）

| | 平均 | 最长 | 超时 |
|---|---|---|---|
| 修前 | 200 ms | 207 ms | 14 轮 |
| **修后** | **0 ms** | **2 ms** | **0 轮** |

即每轮真实工作量是 0–2 ms，原来的 200 ms 全是等待本身。回归测试
`Loop_work_time_excludes_the_scan_interval_wait` 断言"最近几轮的工作耗时必须小于配置间隔且不超时"，
它在旧代码上必红（旧读数恒等于间隔）。

### 一处需要更正的旧结论

上一轮实跑记录里写的"**默认配置下采集循环贴着扫描间隔跑**"是**错的** —— 那是上述埋点错误造成的假象，
不是采集器的问题。已在记忆里更正。

## 追加一轮：工站试读（只读的配置验证）（2026-09）

配一个新工站或改一处点位地址，此前只有一条验证路径：让它真采。而"真采"有六处副作用 ——
消耗当天流水号、写月库、写曲线文件、归档原始文件、往 PLC 触发寄存器回写响应码、MES 出箱入队；
首站真采还会给托盘建会话（托盘码上有在制件时直接报"托盘被盖掉"），记录进库后污染直通率与 NG 榜。

工站配置页新增「试读一次」：按**页面上当前这份配置（含未保存改动）**把该工站的数据只读读一遍 ——
所以不必"先保存、再真采、再改回"，验证就发生在改动旁边。

### 复用与截断

| 环节 | 做法 |
|---|---|
| 编译读计划 | 复用 `StationAcquisitionPlanner.Compile` |
| 读字块 | 复用 `StationBlockReader` + **采集器同一条 `PlcRequestQueue`**（队列是这台 PLC 的唯一连接收口，自建连接会和扫描抢） |
| 解码与判定 | 复用 `ValueCodec` 与 `LimitEvaluator`，与真采同一口径 |
| 触发地址 | **单独读 1 个字** —— 它不在读计划里，而"当前触发值 vs 配置期望值"是地址配错时最直接的证据 |

两处刻意偏离：不经 `CollectEvaluator`（它在产品位空位时会跳过该位点位，而试读要看到每个点位读到了什么）；
文件源点位自己读文件，**绝不复用 `FileSourceReader`**（后者一定先归档再解析，归档本身就是副作用）。

### 四条取舍

1. **停机时不给试读**：采集关闭时主循环直接 continue、不建队列，没有连接可用 → 按钮禁用并说明原因，
   而不是为试读新开一个连接持有者（那会和将来的采集连接错开，某些驱动还不允许双连接）。
2. **曲线默认读**：曲线地址配错在真采前很难发现；但结果只报"点数 / 是否读齐 / 耗时"，不画波形。
3. **文件源点位只读解析**：复用 `JsonFieldReader` / `CsvFieldReader` 两个纯解析器，不归档。
4. **记审计**：动作码 `Trial`；`DisplayLabels` 同步补中文映射、`KnownAuditActions` 条目，
   并归入"非变更类动作"—— 它没改任何数据，说明列该写结果摘要而不是"新增的内容"。

### 实做时挖出来的一个既有坑

`ReadPlan.GetWords` 用 `Array.Copy` 且**不检查源长度** —— 驱动返回的块比请求短时（PLC 断帧、越界）会直接抛。
试读因此加了 `TryGetWords` 前置检查：块短了就如实报"未读到 / 未读齐"，不让整次试读连带失败。
真采路径有外层 `catch` 兜成内部错误，试读没有那层，必须自己防。

### 验证

| 项 | 结果 |
|---|---|
| `dotnet build DataTrace.slnx -c Release` | 0 警告 0 错误 |
| `tests/DataTrace.Tests` | **1347 通过**（新增 `StationTrialTests` 11 项） |
| `tests/DataTrace.Web.Tests` | **254 通过**（新增 `StationsTrialTests` 6 项） |
| `tests/DataTrace.E2E.Tests` | **102 通过**（新增一条真数据下点击试读的用例） |

> **一条操作提醒**：视觉基线只覆盖 `filter-card` / `denied-panel` / `app-shell` / `settings-form` 四张，
> **工站页不在其中**。所以本轮的界面改动不需要、也不该跑 `DATATRACE_E2E_GOLDEN=update` ——
> 误跑会把那四张用本机渲染覆盖（本机环境与录制环境略有差异），凭空多出四个二进制变更。
> 本轮误跑过一次，已用 `git checkout` 还原，并删掉它生成的 `golden/environment.json`。
> **先确认要改的页面在不在截图清单里，再决定要不要 update。**

最后一项是这一轮最关键的一条：`Station_trial_reads_real_values_from_the_line` 在开发态（内置模拟 PLC）
点开 `/config/stations` 真的点一次「试读一次」，要求读到"触发"与"托盘码" —— 只出一句错误提示就算失败。
bUnit 能证明"点了会渲染面板"，证明不了面板里的值真是从 PLC 读回来的。

边界也写进了「本页说明」：工站配置页引导新增"试读（保存前只读核对）"一节，并把原先
"建议用单件仿真/试采集核对"那句改成先试读、再真采。

## 追加一轮：诊断页的三项 P0（判活 / 失败留证 / 并入探活）（2026-09）

诊断页原本能展示"历史上某一段时间跑得怎么样"，却回答不了"**现在还在跑吗**"。
三项改动都指向这一件事 —— 让这一页能自证"我现在看到的是真的"。

| 项 | 问题 | 落实 |
|---|---|---|
| **判活** | 循环卡死后 `Ticks` 停止更新，页面上的平均值/最长/超时数一直停在最后一轮的样子，比"一切正常"还像正常。看板有心跳过期报警、/healthz 有 collector 检查，**唯独这一页看不出来** | chips 行最前面加「最后扫描 X 前」，超过"配置间隔 ×5（下限 2 秒）"即标红，并给出提示：下面的数字是它停下之前的旧记录，不代表现在还在采 |
| **失败留证** | PLC 流水容量 40 条，高频请求下只覆盖几秒 —— 出现过"标题写着失败 12、展开后 40 条全是成功"，`失败 N` 成了点不进去的死数字 | `PlcTrafficLog` 另留一份失败（`SystemDefaults.PlcTrafficFailureCapacity = 20`），`PlcTrafficSnapshot`/`PlcTrafficView` 各加 `RecentFailures`；诊断页把**已被流水挤掉的那部分**列在流水之前 —— 按"早于流水最早一条的时间戳"切分，避免同一行看两遍 |
| **并入探活** | 页面叫"运行诊断"，却看不到那五项检查，用户得另开 /healthz 对照着看 | 抽 `IHealthProbe` 接口（诊断页注入接口，/healthz 端点继续用具体类型），页头下加"系统健康"块：探活项给中文名、异常项附原因 |

### 三个实现取舍

1. **探活不跟着 2 秒刷新跑**（`HealthTickMs = 15 秒`）：目录检查要写探测文件、config-db 要查库、
   disk-free 要读盘 —— 跟着 2 秒跑就成了"观测手段自己制造 IO"，与这一页的设计原则相悖；
   而这几项本来也不是秒级变化的东西。探活自己出错时保留上一份读数，不让整页拖红。
2. **失败列表只列"已被挤掉"的**：两份列表会重叠（断线时 Recent 里本来就全是失败，那一份没有额外信息）。
   服务端热路径不做比对（每条请求都要生成快照），由页面渲染时按时间戳切分。
3. **`IHealthProbe` 抽接口**：`HealthProbe` 要连着配置库、数据目录、采集器心跳才构得出来，bUnit 里造不动；
   抽端口让诊断页可测（与试读那轮的 `IPlcQueueAccess` 同一手法）。

### 验证（每步三套）

| 步骤 | build | 核心 | Web | E2E |
|---|---|---|---|---|
| P0-1 判活 | 0/0 | 1347 | 256 | 102 |
| P0-2 失败留证 | 0/0 | 1350 | 258 | 102 |
| P0-3 并入探活 | 0/0 | 1350 | 260 | 102 |

新增测试 11 项：`PlcTrafficLog` 的失败保留与上限（3）、诊断页的停摆识别 / 失败列表 / 健康块（8）。
「本页说明」同步：诊断页引导新增"系统健康"一节（写明它为何每 15 秒才复查一次），
"页面用途"补上系统健康，"采集循环怎么读"把"先看最后扫描距今"写进第一条排查动作，
"PLC 通信怎么看"补失败另存的理由。

## 追加一轮：采集循环趋势图（2026-09）

"平均 5 ms"会吞掉偶发 200 ms 的尖峰 —— 平均值是聚合，**形状**才是排查线索。
诊断页采集循环块在表格上方加折线，复用 `LineChart`：

- **两条线**：每轮耗时（`ChartColors.Primary`）与**当时的扫描间隔**（`ChartColors.Secondary`）。
  间隔画成第二条线而不是一条静态基准线，是因为间隔可能被改过 —— 每轮用它自己的值，
  改动在图上是一条看得见的台阶。
- `Ticks` 是倒序（最新在前），折线要从左往右走时间，构造时反转；首尾标注取最早/最新一轮的时分秒。
- 空态不渲染图表：页面本身已有"还没有采集循环记录"，图表再渲染一遍就重复了。

「本页说明」的"采集循环怎么读"补上曲线读法（尖峰在平均值里看不见、间隔改动是台阶）。

## 追加一轮：补传队列的"毒丸"与双开（2026-09）

两项回答的是同一类问题：**一次意外之后，系统能不能自己恢复**。

| # | 问题 | 落实 | 落点 |
|---|---|---|---|
| 1 | **一条"永远失败"的缓存件能把整条补传队列堵死**。写库成功但缓存没删掉（提交后进程被杀、删文件被占用）留下的缓存件，重放时会撞流水号唯一索引而永久失败；而重放是"从最早一条开始、遇失败即止"，它后面所有件永远轮不到 —— 诊断页上只有一条反复失败的老件，队伍越排越长 | 重放前按流水号查重（库不存在即"没有"，不新建月库），命中就按已入库收尾：删缓存、记一行"补传跳过 …记录已在库"。后台循环与手动单条补传共用同一条路径，因此手动点"立即补传"同样自愈 | `ICollectQuery.ExistsBySerialAsync`（`Application/Runtime/RuntimeContracts.cs`）、实现（`Infrastructure/Persistence/RuntimeStore.Queries.cs`）、重放（`Collector/SpoolReplayRunner.cs`） |
| 2 | **应用侧没有单实例保护**。部署脚本层只有 pid/端口检查，防不住"服务 + 手工 exe""没走脚本的启动"这类双开：两个进程同时扫同一台 PLC，同一件各拿一个流水号、入库两份，补传队列互相抢，MES 重复推送，而两边日志各记各的 | 启动时对数据目录加独占锁文件（`data/.datatrace.instance.lock`），拿不到就写一行 FTL（带占用者 pid）并以退出码 1 退出。判定单位是**数据目录**而非机器：一台机器上多个客户实例是正常部署；进程异常退出时句柄由操作系统关闭，锁不会残留、也不需要手工清 | `Web/Services/SingleInstanceGuard.cs`、`Web/Program.cs` |

### 三处取舍

1. **没有把"失败即止"改成"失败 N 次自动隔离退队"**：失败即止是刻意设计（队列里的失败几乎都是同一个系统性原因 —— 库盘满、月库锁住，继续往下试只会刷日志、摊薄"卡了几次"这个数字）；失败件在诊断页可见、等待超 5 分钟有报警、可人工丢弃。本轮只消掉"确定性永远失败"这条具体的堵死路径，不动那条设计。
2. **锁用文件而不是命名互斥体**：命名互斥体的判定单位是机器，而这里要保护的是"哪个数据目录"；锁文件还让运维一眼看得见目录正被谁占着。
3. **占用者 pid 写进锁文件**：锁以 `FileShare.Read` 持有，只有本进程能写、别的进程能读 —— 抢占失败的一方因此能在日志里写出"进程号 N 正在使用"，不用只回一句"被占用"。

### 验证

| 项 | 结果 |
|---|---|
| `dotnet build DataTrace.slnx -c Release` | **0 警告 0 错误** |
| `tests/DataTrace.Tests` | **1355 项全部通过**（新增 1 项：已入库的缓存件被跳过、队列继续走且只保留一条记录） |
| `tests/DataTrace.Web.Tests` | **283 项全部通过**（新增 5 项：抢锁被拒、释放后可重抢、不同目录互不影响、占用者 pid 可读/无锁时为 null） |
| `tests/DataTrace.E2E.Tests`（`DATATRACE_E2E_GOLDEN=auto`） | **102 项全部通过** |
| 真实双开冒烟 | 实例 A 起来后启实例 B（同一 DataRoot、不同端口）：B 在 1 秒内退出（退出码 **1**），日志一行 FTL `已有 DataTrace 实例正在使用数据目录 …（进程号 19816）`，锁文件里读得到该 pid |

两点如实说明：

- 新增的查重让 `SpoolReplayRunner` 依赖 `ICollectQuery`，诊断页的 bUnit 用例原先只注册了 `ICollectWriter` 替身 —— 全量跑出的唯一一处 Web 失败即由此而来（已补注册替身，返"库里没有"）。生产 DI 里两者本就都注册，行为不变。
- 全量首跑时 `CollectionHostedServiceTests.Loop_work_time_excludes_the_scan_interval_wait` 偶发红一次（工作耗时 246ms > 间隔 200ms，机器当时刚跑过冒烟、负载偏高）；隔离重跑 3 次与全量重跑均通过。与本文件此前记录过的时序敏感用例同类。

