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
