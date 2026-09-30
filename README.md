# DataTrace 产线数据采集系统

.NET 8 Blazor Server 应用：多品牌 PLC 采集、托盘会话、曲线外置、按月 SQLite 分库、Web 查询与报表。

## 构建

```bat
dotnet build DataTrace.slnx -c Release
```

- **SDK 版本由 `global.json` 固定为 .NET 10**（`.slnx` 解决方案格式需要它），各工程仍 target `net8.0`，
  跑起来需要 8.0 运行时。CI 因此同时装 8.0.x 与 10.0.x（见 `.github/workflows/dotnet-test.yml`）。
- 包版本集中在 `Directory.Packages.props`（CPM）：`.csproj` 里只写 `Include`，改版本改一处。
- `TreatWarningsAsErrors=true`：多出一个告警构建就红。确有需要临时放行时用 `WarningsNotAsErrors` 逐条列出。

## 运行

```bat
cd src\DataTrace.Web
dotnet run
```

浏览器打开 http://localhost:5080

**开发环境**（`ASPNETCORE_ENVIRONMENT=Development`）会种入演示账号，口令写在源码里，仅限本机：

| 用户 | 密码 | 角色 |
|---|---|---|
| admin | Admin@123 | 管理员 |
| engineer | Engineer@123 | 工程师 |
| operator | Operator@123 | 操作员 |
| viewer | Viewer@123 | 访客 |

首次启动会写入演示产线（3 工站、每托盘一件产品、位移压力曲线）并连接**模拟 PLC**。工程师在「PLC仿真」页即可走通采集-存储-查询。

### 生产环境的账号

生产**不种演示账号**。首启动只创建一个引导管理员 `admin`：

- 口令随机生成，只在首启动日志里打印一次（`data\logs\datatrace-*.log`，搜"引导管理员"）；
- 该账号被标记为「必须改密」，登录后会被拦到 `/change-password`，改完才能进其它页面；
- 升级上来的老库若 `admin` 仍在使用源码里的演示口令，启动时会自动补上这个标记。

要指定初始口令（例如批量部署脚本要可控）用配置项 `Seed:AdminPassword`：

```bat
set Seed__AdminPassword=YourOwnInit1
```

生产环境**无法**通过任何开关种入演示账号。非生产环境（Staging 等）若确实需要演示账号
（例如在关掉开发态免登录的前提下跑端到端登录用例），可显式打开：

```bat
set Seed__DemoUsers=true
```

## 传输安全（HTTPS）

默认只监听 HTTP（`Kestrel:Endpoints:Http`）。要开 HTTPS，加上端点与证书即可，**代码不用改**：

```jsonc
"Kestrel": {
  "Endpoints": {
    "Http":  { "Url": "http://0.0.0.0:5080" },
    "Https": { "Url": "https://0.0.0.0:5443",
               "Certificate": { "Path": "C:\\DataTrace\\certs\\site.pfx", "Password": "***" } }
  }
}
```

配了 HTTPS 端点之后应用会自动：把会话 Cookie 钉死在 `Secure`（默认的 `SameAsRequest` 会在一次 http
访问时把带凭据的 Cookie 明文发出去），非 Development 下对浏览器下 HSTS。

坚持纯 HTTP 部署时请至少做到：网络隔离（VLAN / 防火墙只放行工控网段）+ 首装立即改掉初始口令，
`deploy\CHECKLIST.md` 里把这两条写成验收前置条件。

## 探活

```bat
curl http://localhost:5080/healthz
```

返回 JSON，`status` 为 `healthy` / `unhealthy`（不健康时 HTTP 503）。检查项：

| 名称 | 含义 |
|---|---|
| `config-db` | 配置库连得上、读得动 |
| `data-root` / `runtime-dir` | 数据盘可写（装到 Program Files 或数据盘掉线时会在这里先红） |
| `disk-free` | 数据盘剩余空间（低于 `SystemDefaults.MinDiskFreeMegabytes` 判不健康：磁盘写满之后才会开始落库失败，那时已经晚了） |
| `collector` | 采集器主循环心跳还新鲜（默认 60 秒无心跳判停：界面看着正常但数据早就停更了） |

端点匿名可访问，因此只回结论与错误摘要，不回显安装路径。可直接接 Windows 服务守护或客户监控系统。

## UI 回归测试

`tests/DataTrace.E2E.Tests` 用本机 Edge 起一个隔离实例跑真浏览器用例，其中视觉回归分两种：

- **几何断言**（任何机器都稳定）：12 条路由不出现整页横向滚动、430px 宽下仍不溢出、
  筛选卡里的按钮与输入框不互相压盖、表格滚动容器有确定高度。
- **像素基线**（`golden/*.png`，随仓库提交）：`DATATRACE_E2E_GOLDEN` 控制——
  `update` 重录基线（改完样式跑一次，人眼确认再提交，同时会写下录制环境指纹
  `golden/environment.json`）、`compare`（本地默认，严格比）、`auto`（CI 用，只在"基线存在且
  指纹与本机一致"时才比，否则退化成只验"截得出图"）、`off` 完全不比。
  一串基线只对**同一操作系统 + 同一浏览器版本**有意义，所以跨环境时 `auto` 会主动让路，
  不抖假红；想在某台 runner 上真比，就在那台机器上 `update` 一次并提交 `golden/`。
  基线只挑没有实时数据的外壳区域；看板每几秒刷新一次，那种页面做像素比对必然天天红。

## Windows 服务

先发布，再把整个目录拷到工控机（例如 `C:\DataTrace`）：

```bat
dotnet publish src\DataTrace.Web -c Release -o publish
sc create DataTrace binPath= "C:\DataTrace\DataTrace.Web.exe" start= auto
sc start DataTrace
```

监听 `http://0.0.0.0:5080`，工控机与平板可通过局域网 IP 访问。

服务的工作目录是 `C:\Windows\System32`，不是安装目录；应用的**内容根固定取 exe 所在目录**，
所以 `appsettings.json`、`wwwroot` 都能正常读到（若按工作目录找，端口会悄悄退回 5000、
日志级别与 `DataRoot` 一起失效）。因此 `sc create` 不需要额外指定工作目录。

换端口时别只改 `ASPNETCORE_URLS`：`appsettings.json` 里的 `Kestrel:Endpoints:Http:Url`
优先级更高，会**静默**把它覆盖掉（现象是服务照样起在 5080，日志里没有任何提示）。
改监听地址要改配置文件，或用同名配置项覆盖：

```bat
set Kestrel__Endpoints__Http__Url=http://0.0.0.0:5100
```

## 数据目录

`data/config.db` 配置与用户  
`data/runtime/data_yyyyMM.db` 月库  
`data/curves/` 曲线文件  
`data/archive/` 文件源工站读到的原始 JSON 归档  
`data/spool/` 写库失败补传  
`data/logs/` 日志（单文件上限 32 MB、最多留 30 份，可用 `Logging:File:FileSizeLimitMb`
与 `Logging:File:RetainedFileCount` 覆盖；日志总量封顶在两者相乘以内，避免异常风暴把数据盘写满）  
`data/.datatrace.instance.lock` 单实例锁

同一数据目录**只允许一个实例**：启动时对这个锁文件加独占，拿不到就写一行日志
（带占用者 pid）并以退出码 1 退出。防的是"服务 + 手工 exe"这类双开 —— 两个进程同时采
同一台 PLC，同一件会各拿一个流水号、入库两份，补传队列也会互相抢。要再起一个就先把
在跑的那个停掉（服务：`sc stop DataTrace`；控制台：`deploy\stop.ps1`）。
需要同机跑多个实例（多客户）时，各自配不同的 `DataRoot` 即可，互不影响。

`DataRoot` 可以指到别的盘（相对路径按 exe 目录解析），**日志也一起跟着走**——
装在 `C:\Program Files` 下时服务对安装目录通常没有写权限。

保留策略默认 3 年，按整月删除。

### 按一次采集查日志

日志级别配在 **`Serilog:MinimumLevel`** 段下（不是 `Logging:LogLevel` —— 应用用
`UseSerilog` + `ReadFrom.Configuration`，只读 `Serilog` 段，写 `Logging` 段一个字都不会生效）。

一次采集从触发到落库的每一行都带同两份事实，格式是
`{ CollectStation: "ST020", CollectTrigger: "16:10:54.846" }`；落库及其之后的行还会多一份 `CollectSerial`。
所以排查现场那句"某件丢了 / 某件慢了"：

1. **用流水号搜** → 命中的是每件的锚点行 `采集完成 20260930-000073 结果 … 耗时 … 回写 …`，
   同一个流水号在几个工站各有一行，就是这件的完整产线路径；
2. 由锚点行往上，同一个作用域里的行是这一件的读取、判定、落库、故障、回写；
3. 如果落库失败，会有一行 `已转存本地缓存 <文件名>`；拿这个文件名去搜，就能对上补传那边的
   `补传成功/失败 <文件名> <流水号>`。

## PLC 品牌

模拟器、三菱 MC 3E、西门子 S7、Modbus TCP、欧姆龙 FINS（后四者经 IoTClient）。Float 字序可配，字符串 ASCII。

## 平台说明

本应用面向 Windows 工控机交付，`DataTrace.Web` 里有若干 Windows 专有实现，换平台需要先替换它们：

- `Services/WindowsJsonFileDialog.cs`（选文件路径的能力，失败时退到手工输入）；
- Windows 服务宿主（`Microsoft.Extensions.Hosting.WindowsServices`）；
- E2E 的像素比对用 `System.Drawing.Common`，且驱动本机 Edge。

除此之外的分层（Domain / Application / Shared / Plc / Collector / Infrastructure）都是跨平台的。
