# DataTrace MCP 接入方案

> 版本：2026-11-06  
> 范围：为 DataTrace.Web 增加 MCP（Model Context Protocol）服务端能力，让 AI 客户端可以查询产线状态、明细、报表、报警和质量异常。  
> 结论：**进程内挂载 Streamable HTTP 端点，默认关闭，只读优先，写入分期开放。**
>
> **实施状态（2026-11-06）**：一期只读版已落地，共 **18 个只读工具**，并新增 **3 个 MCP 资源**（工站/型号/报警目录）与 **3 个提示模板**（交接班、质量根因、报表解读）。数据库令牌与管理员令牌页、Bearer 认证、角色矩阵、限流、审计与配置库迁移均已实现；`Mcp:Enabled` 默认关闭。二期写入工具（报警接手、质量异常创建/流转）也已实现，但**只有 `Mcp:AllowWrites=true` 时才注册**，且需要 `write` scope。`system_health` 工具因依赖 Web 层的 `IHealthProbe`，实现放在 `DataTrace.Web/Services` 并单独注册，其余工具在 `DataTrace.Mcp` 工程。已知限制：业务审计覆盖成功的工具调用；鉴权拒绝发生在工具执行前，由 ASP.NET Core 请求日志记录，不写业务审计表。

## 1. 结论摘要

- 使用官方 `ModelContextProtocol.AspNetCore` **2.3.1**（已确认支持 `net8.0`），在现有 `DataTrace.Web` 进程内挂 `/mcp`，不新起进程、不改采集链路。
- 传输只用 **Streamable HTTP**；旧 HTTP+SSE 双端点模式已废弃，不采用。
- 端点**默认关闭**（`Mcp:Enabled=false`）；开启后必须携带独立 Bearer Token，**不接受现有登录 Cookie**。
- 第一批只开放**只读工具**：状态、配置目录、采集明细、会话履历、报表、趋势、SPC、报警、质量异常、审计（管理员）。
- PLC 写入、配置保存、用户管理、备份恢复**永久排除**在 MCP 之外；报警接手/质量异常操作作为第二批，需单独开关与角色校验。
- Token 绑定现有用户，角色变化、禁用、删除立即影响 Token；每次工具调用写审计，`Source="MCP"`。
- 所有查询强制时间范围、分页和行数上限；曲线只返回摘要或降采样，不返回原始 `float[]`。
- 工作量：只读版约 **3–4 人日**；含 Token 管理界面与写入能力约 **8–11 人日**（不含 .NET 版本升级）。

## 2. 目标与非目标

### 目标

1. AI 助手（Claude、Qoder、Cursor、MCP Inspector 等）能通过标准 MCP 协议读取 DataTrace 的业务数据。
2. 复用现有 Application 层服务与权限规则，不复制一套查询 SQL。
3. 默认安全：不开启就没有攻击面；开启后鉴权、限流、审计、脱敏齐全。
4. 工具输入输出有明确口径（时间、单位、分页、时区），适合模型调用。

### 非目标

- 不做公网多租户 MCP 服务。
- 不通过 MCP 直接操作 PLC、写采集记录、改配置、管理用户、操作备份。
- 不提供任意 SQL 查询、文件读取或命令执行。
- 不替代现有 REST/API 规划；本期不新增通用 REST 层。

## 3. 现状与约束

| 项 | 现状 | 对 MCP 的影响 |
|---|---|---|
| 运行时 | .NET 8 / Blazor Server / MudBlazor | MCP SDK 2.3.1 支持 net8.0，可直接进程内集成 |
| 分层 | Domain / Application / Infrastructure / Collector / Web | 工具只依赖 Application 接口，不直接碰 EF 或 PLC |
| 鉴权 | ASP.NET Core Identity Cookie，12 小时滑动过期 | MCP 客户端无法走浏览器登录，必须新增 Token 认证 |
| 开发态 | Development 自动以 admin 登录所有请求 | `/mcp` 必须显式跳过该中间件，否则本地测试等于无鉴权 |
| 授权 | 角色：Administrator / Engineer / Operator / Viewer；策略 `Config`、`Admin` | 工具级授权要复用角色语义，不能只做端点级校验 |
| 部署 | Windows 服务，单实例锁，监听 0.0.0.0:5080 | MCP 与 Web 同进程，天然单实例；不需要额外锁 |
| 数据 | 配置库 `config.db` + 按月 `runtime/data_yyyyMM.db`（SQLite WAL） | 长查询会与采集写入争用，必须限范围、限行、设超时 |
| 构建 | CPM + `TreatWarningsAsErrors=true` | 新包版本只在 `Directory.Packages.props` 维护，构建必须 0 警告 |
| 支持周期 | .NET 8 LTS 于 2026-11-10 结束支持 | 不阻塞本期，但应在下一个迭代升级到 .NET 10 LTS |

## 4. 选型

### 4.1 进程内 + Streamable HTTP

| 方案 | 结论 | 原因 |
|---|---|---|
| 进程内 ASP.NET Core `/mcp` | **采用** | 直接复用 DI、用户体系、Application 服务、单实例锁；部署零新增进程 |
| 独立 MCP 进程连数据库 | 不采用 | 绕过业务规则与权限，SQLite 双进程读写风险，配置与审计割裂 |
| stdio 子进程 | 不采用 | 客户端在另一台机器时不可用；Windows 服务形态无法被远程 AI 客户端拉起 |
| HTTP+SSE 旧传输 | 不采用 | 规范已废弃，新项目直接用 Streamable HTTP |

### 4.2 SDK 与版本

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="ModelContextProtocol.AspNetCore" Version="2.3.1" />
<PackageVersion Include="ModelContextProtocol.Core" Version="2.3.1" />
```

- `ModelContextProtocol.AspNetCore` 放在 `DataTrace.Web`（提供 `AddMcpServer`、`MapMcp`、HTTP 传输）。
- `ModelContextProtocol.Core` 放在新增的 `DataTrace.Mcp` 工具库（只用到工具特性与类型）。
- 版本锁定，不使用 `--prerelease`；升级前先跑集成测试并核对 MCP 规范版本。

## 5. 总体架构

```
AI Client (Claude / Qoder / Cursor / Inspector)
        │  Streamable HTTP + Authorization: Bearer dtmcp_...
        ▼
┌──────────────────────── DataTrace.Web (single process) ────────────────────────┐
│ Kestrel :5080                                                                  │
│  ├─ /            Blazor Server（现有，Cookie 认证）                             │
│  ├─ /healthz     现有探活                                                       │
│  └─ /mcp         MCP 端点                                                       │
│       ├─ McpTokenAuthenticationHandler（独立 Scheme，不认 Cookie）              │
│       ├─ 速率限制（按 Token）                                                   │
│       ├─ ToolGuard（角色 / scope / 写入开关）                                   │
│       └─ DataTrace.Mcp Tools                                                   │
│               │ 只调用 Application 接口                                         │
│               ▼                                                                │
│  IRuntimeStatusHub · IRuntimeStore · IReportService · ISpcService              │
│  IAlarmIncidents · IQualityIncidentService · IConfigRepository · IAuditLogger  │
└────────────────────────────────────────────────────────────────────────────────┘
        │                                    │
        ▼                                    ▼
  config.db（配置/用户/Token/审计）      runtime/data_yyyyMM.db（WAL，只读查询）
```

关键边界：

- 工具**不注入** `DbContext`、`IConfigRepository` 的写方法、`ICollectWriter`、PLC 驱动。
- 工具输出使用独立的 MCP DTO，不直接序列化 EF 实体，避免导航属性、内部字段和敏感信息泄漏。
- 工具方法都是异步并透传 `CancellationToken`，超时由请求取消统一控制。

## 6. 认证与授权

### 6.1 Token 模型

新增配置库实体 `McpAccessToken`（ConfigDbContext + 迁移）：

| 字段 | 说明 |
|---|---|
| `Id` | 主键 |
| `UserName` | 绑定 `ApplicationUser`，角色与启用状态实时生效 |
| `Name` | 用途说明，例如“夜班交接助手” |
| `Prefix` | Token 前 8 位，用于定位与界面识别 |
| `TokenHash` | 32 字节随机数的 SHA-256；Token 本身高熵，无需慢哈希 |
| `Scopes` | `read` / `write`，逗号分隔 |
| `CreatedAt` / `CreatedBy` | 创建信息 |
| `ExpiresAt` | 必填，默认 90 天，最长 365 天 |
| `RevokedAt` | 撤销时间，非空即失效 |
| `LastUsedAt` | 最近使用时间（最多每 5 分钟回写一次，避免频繁写配置库） |

Token 格式：`dtmcp_<id>_<base64url(32B)>`。明文只在创建时显示一次，之后只存哈希。校验用固定时间比较。

管理入口：`/config/mcp-tokens`（Administrator），支持创建、撤销、查看前缀/绑定用户/范围/过期时间/最近使用。创建与撤销都写审计。

### 6.2 认证链

- 新增独立认证 Scheme `McpToken`，只读取 `Authorization: Bearer dtmcp_...`。
- `/mcp` 的授权策略**只列** `McpToken` scheme，因此现有 Identity Cookie 无法通过；浏览器即使带 Cookie 也进不来，避免 CSRF/DNS rebinding 类问题。
- Development 的自动 admin 登录中间件对 `/mcp` 路径跳过，防止本地“无 Token 也能用”的假象。
- 用户被禁用、删除或角色变化时，下一次请求即失效（`UserManager` 每次校验，或最多 30 秒缓存）。
- 未启用 MCP 时不注册端点、不注册 Scheme，`/mcp` 返回 404。

### 6.3 授权矩阵

端点级只校验 Token 有效并具有 `read` scope；**每个工具内部再校验角色**，因为一个端点承载全部工具。

| 工具组 | Viewer | Operator | Engineer | Administrator |
|---|---|---|---|---|
| 状态 / 工站 / 点位 / 查询 / 报表 / 趋势 / SPC | 允许 | 允许 | 允许 | 允许 |
| 报警列表、质量异常列表与详情 | 允许 | 允许 | 允许 | 允许 |
| 配置目录（脱敏） | 拒绝 | 拒绝 | 允许 | 允许 |
| MES Outbox 状态 | 拒绝 | 拒绝 | 允许 | 允许 |
| 审计日志 | 拒绝 | 拒绝 | 拒绝 | 允许 |
| 报警接手 / 质量异常操作（二期） | 拒绝 | 允许 | 允许 | 允许 |
| 写入类工具（二期） | 需 `write` scope + `Mcp:AllowWrites=true` | 同左 | 同左 | 同左 |

### 6.4 限流与防滥用

- 按 Token 固定窗口限流，默认 60 次/分钟，超限返回 429；可配置。
- 重量级查询（报表、SPC）加并发闸门（例如全局 4 个并发），保护 SQLite 写入。
- 单次请求超时 30 秒；取消时透传到 EF 查询。
- 请求体上限沿用 Kestrel 默认；不接受文件上传。
- 生产建议只监听内网地址或用防火墙限制访问来源；有 HTTPS 时强制 `https`，纯 HTTP 需显式 `Mcp:AllowInsecureHttp=true` 才允许（默认拒绝启动或拒绝请求）。

## 7. 工具目录

### 7.1 一期：只读（当前 18 个）

工具名用英文 `snake_case`；描述写清时间口径、单位、分页上限。所有时间入参要求 ISO 8601 带偏移，输出统一带 `+08:00`。

| 工具 | 作用 | 复用服务 | 限制与说明 |
|---|---|---|---|
| `system_health` | 配置库、数据盘、磁盘空间、采集心跳、在途任务超龄 | `IHealthProbe.CheckAsync` | 只返回结论与摘要 |
| `get_line_status` | 当前工站状态、PLC 连接、当前型号、最近记录、采集就绪 | `IRuntimeStatusHub` | 纯内存，快照 |
| `list_stations` | 工站列表与启用状态 | `IConfigRepository.GetStationsAsync` | 精简字段 |
| `list_tags` | 指定工站的点位、单位、用途、规格限 | `IConfigRepository` / 配置快照 | 必须给 `stationId` |
| `query_records` | 采集明细筛选与分页 | `IRuntimeStore.QueryAsync` | 默认当天；范围 ≤31 天；`take` ≤100；沿用 `NextCursor` |
| `get_record_detail` | 单条记录详情（含标签值摘要、曲线摘要） | `GetRecordAsync` + 会话关联 | 不返回原始曲线数组 |
| `query_sessions` | 托盘会话履历 | `QuerySessionsAsync` | 范围 ≤31 天；`take` ≤100 |
| `get_report` | 产量/直通率/停线/不良 Top/预警 Top | `IReportService` 各方法 | `kind` 枚举；范围 ≤31 天 |
| `get_trend` | 点位趋势 | `IReportService.GetTrendAsync` | 最多 500 点，按需降采样到 200 |
| `get_process_capability` | 过程能力与判异 | `ISpcService.GetProcessCapabilityAsync` | 范围 ≤31 天；样本上限 2000 |
| `list_alarms` | 待关注 + 最近已关闭报警 | `IAlarmIncidents` | `take` ≤50 |
| `list_quality_incidents` | 质量异常筛选与分页 | `IQualityIncidentService.ListAsync` | `take` ≤50 |
| `get_quality_incident` | 异常详情、证据、活动 | `IQualityIncidentService.GetAsync` | 证据只返回元数据 |
| `list_work_orders` | 工单列表/当前工单 | `IConfigRepository.GetWorkOrdersAsync` | 精简字段 |
| `get_operational_config` | 型号、当前配方限值、工站/点位目录（脱敏） | `IConfigRepository.GetSnapshotAsync` | Engineer+；不返回 PLC 凭据 |
| `get_mes_outbox_status` | MES 出站队列待推送、积压与最近错误 | `IConfigRepository.GetMesOutboxStatusAsync` | Engineer+ |
| `get_collection_diagnostics` | 采集循环耗时、站级 P95/P99、在途/超龄任务、PLC 失败摘要 | `ICollectorDiagnostics` | 内存快照，不查历史库 |
| `get_audit_logs` | 审计查询 | `IAuditLogger.QueryAsync` | Administrator；范围 ≤7 天；`take` ≤200 |

如果工具数量继续增长，优先合并同类查询（例如报表已用 `kind` 合并），避免模型选错工具。

### 7.2 资源与提示模板

- 资源：`datatrace://stations`、`datatrace://recipes`、`datatrace://alarms/open`，JSON 格式，用于稳定目录与报警快照。
- 提示模板：`shift_handover`、`quality_root_cause`、`report_reading`，指导客户端按正确口径调用工具。
- 资源与提示同样挂 `read` 授权策略，经过 Bearer 认证与审计。

### 7.3 二期：写入（默认关闭）

只有 `Mcp:AllowWrites=true` 且 Token 有 `write` scope、用户角色满足时才注册/放行：

| 工具 | 作用 | 复用服务 | 额外要求 |
|---|---|---|---|
| `acknowledge_alarms` | 接手报警（单个/批量） | `IAlarmIncidents.AcknowledgeManyAsync` | 已接手视为幂等成功 |
| `create_quality_incident` | 创建质量异常 | `IQualityIncidentService.CreateAsync` | 客户端提供 `requestId`（幂等） |
| `update_quality_incident` | 接手/指派/备注/提交复核/复核 | 对应服务方法 | 必须带 `expectedVersion`，冲突返回明确错误 |

写入工具的通用约束：

- 不接受“任意字段更新”，只暴露语义化动作。
- 操作者身份取 Token 绑定的用户，不接受客户端传用户名。
- 每个动作写审计；返回新的版本号。
- 失败不重试；由客户端决定是否重试，幂等键兜底。
- PLC 回写、配置保存、用户管理、备份操作不开放。

## 8. 输出与上下文控制

模型上下文很贵，工具返回必须“够用就好”：

- 明细分页默认 50、上限 100；报表按聚合结果返回，不返回原始行。
- 趋势最多 500 点；超过时按等距降采样，并在结果里标注 `downsampled=true`。
- 曲线只返回每条序列的判定、峰谷/均值/超限原因；原始 `float[]` 不返回。
- 长文本（错误信息、备注、证据说明）截断到 2000 字符并标注截断。
- DTO 字段用 `camelCase`，枚举输出中文标签 + 原始值，时间输出 ISO 8601。
- 所有列表返回 `total` 与 `hasMore`/`nextCursor`，便于模型决定是否继续翻页。

## 9. 代码落点

### 9.1 新增工程

```
src/DataTrace.Mcp/
  DataTrace.Mcp.csproj          # net8.0，引用 Application/Shared + ModelContextProtocol.Core
  Tools/SystemTools.cs
  Tools/QueryTools.cs
  Tools/ReportTools.cs
  Tools/AlarmTools.cs
  Tools/QualityTools.cs
  Tools/OperationsTools.cs
  Dtos/...
  McpCallContext.cs             # TokenId/UserName/Roles/Scopes/SourceIp/CorrelationId
  ToolGuard.cs                  # RequireRole / RequireWrite / 统一错误
  ToolAudit.cs                  # 审计封装
```

- `DataTrace.Web` 引用 `DataTrace.Mcp`，并引用 `ModelContextProtocol.AspNetCore`。
- 工具类不注册为 HostedService；由 SDK 按请求从 DI 解析，构造函数注入 scoped 服务。
- `McpCallContext` 为 scoped，从 `IHttpContextAccessor` 读取已认证 Principal、Token Id、远端 IP 与请求 id；工具只通过它做授权和审计，不直接使用 `HttpContext`。
- `DataTrace.Mcp` 不引用 Infrastructure，防止工具绕过 Application 直接访问数据库。

Token 与鉴权相关落点：

```
src/DataTrace.Infrastructure/Identity/McpTokenStore.cs     # IMcpTokenStore + EF 实现
src/DataTrace.Infrastructure/Persistence/Migrations/Config/ # McpAccessToken + 索引
src/DataTrace.Web/Security/McpTokenAuthenticationHandler.cs
src/DataTrace.Web/Security/McpTokenDefaults.cs
src/DataTrace.Web/Components/Pages/Config/McpTokens.razor   # 管理员 Token 管理页
```

配置库迁移按仓库现有流程生成：

```bat
dotnet ef migrations add AddMcpAccessToken --project src\DataTrace.Infrastructure --startup-project src\DataTrace.Infrastructure --context ConfigDbContext --output-dir Persistence\Migrations\Config
```

### 9.2 Web 接线（示意）

```csharp
// Program.cs，Build 之前
if (builder.Configuration.GetValue<bool>("Mcp:Enabled"))
{
    builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "DataTrace", Version = "1.0.0" };
    })
    .WithHttpTransport(o => o.Stateless = true)   // 具体属性名以 SDK 2.3.x 为准
    .WithToolsFromAssembly(typeof(SystemTools).Assembly);

    builder.Services.AddAuthentication()
        .AddScheme<McpTokenAuthenticationOptions, McpTokenAuthenticationHandler>(
            McpTokenDefaults.Scheme, _ => { });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("McpAccess", p => p
            .AddAuthenticationSchemes(McpTokenDefaults.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim("scope", "read"));
    });

    builder.Services.AddRateLimiter(o => o.AddPolicy("mcp", ...));
}
```

```csharp
// 开发态自动登录中间件：跳过 /mcp
var isMcpEndpoint = path.StartsWithSegments("/mcp");
if (!isAuthEndpoint && !isMcpEndpoint && autoSignInAdminInDevelopment && ...)
```

```csharp
// 中间件之后
if (mcpEnabled)
{
    app.UseRateLimiter();             // .NET 8 共享框架自带，无需额外包
    app.MapMcp()                      // 默认 /mcp
       .RequireAuthorization("McpAccess")
       .RequireRateLimiting("mcp");
}
```

工具方法示意：

```csharp
[McpServerToolType]
public sealed class QueryTools(
    IRuntimeStore runtime,
    McpCallContext call,
    ToolAudit audit)
{
    [McpServerTool, Description("按时间、工站、托盘、流水号、判定筛选采集明细。时间范围最多 31 天，take 最大 100。")]
    public async Task<RecordPageDto> query_records(
        [Description("ISO 8601，含时区偏移")] DateTimeOffset from,
        [Description("ISO 8601，含时区偏移")] DateTimeOffset to,
        int? stationId = null,
        string? palletCode = null,
        string? serialNo = null,
        int take = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var (fromLocal, toLocal) = call.ClampRange(from, to, maxDays: 31);
        take = Math.Clamp(take, 1, 100);

        var result = await runtime.QueryAsync(new CollectQueryRequest
        {
            From = fromLocal, To = toLocal, StationId = stationId,
            PalletCode = palletCode, SerialNo = serialNo,
            Take = take, AfterCursor = cursor
        }, cancellationToken);

        await audit.RecordAsync("mcp.query_records", result.Total, cancellationToken);
        return RecordPageDto.From(result);
    }
}
```

### 9.3 认证处理器要点

```csharp
protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
{
    var header = Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer dtmcp_", StringComparison.Ordinal))
        return AuthenticateResult.NoResult();

    var token = await _tokens.ValidateAsync(header[7..], Context.RequestAborted);
    if (token is null) return AuthenticateResult.Fail("invalid token");

    var user = await _users.FindByNameAsync(token.UserName);
    if (user is null || !user.IsEnabled) return AuthenticateResult.Fail("user disabled");

    var claims = new List<Claim> { new(ClaimTypes.Name, user.UserName!), new("token_id", token.Id.ToString()) };
    claims.AddRange(await _users.GetRolesAsync(user).Select(r => new Claim(ClaimTypes.Role, r)));
    claims.AddRange(token.Scopes.Select(s => new Claim("scope", s)));

    return AuthenticateResult.Success(
        new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, McpTokenDefaults.Scheme)),
                                 McpTokenDefaults.Scheme));
}
```

校验顺序：前缀/格式 → 按 `Id` 查 Token → 未撤销且未过期 → 固定时间比较哈希 → 用户启用 → 角色/scope 组装 → 异步更新 `LastUsedAt`（节流）。校验失败统一 401，不区分“Token 不存在/过期/用户禁用”，避免信息泄漏。

### 9.4 审计

每次工具调用写一条 `AuditLog`：

- `UserName`：Token 绑定用户
- `Action`：`mcp.<tool_name>`
- `EntityType`：`MCP`
- `Outcome`：`Success` / `Denied` / `Failed`
- `Source`：`MCP`
- `SourceIp`：连接远端 IP
- `CorrelationId`：请求 id
- `NewValue`：参数摘要（不含全量业务数据）+ 返回行数 + 耗时，截断

失败和越权也写审计；审计写入失败不回滚业务（只读查询没有业务写入），但记录错误日志并计入健康诊断。

## 10. 配置

```jsonc
"Mcp": {
  "Enabled": false,
  "RequireHttps": true,          // true 时必须走 HTTPS；内网纯 HTTP 需显式关闭
  "AllowInsecureHttp": false,
  "AllowWrites": false,
  "MaxRangeDays": 31,
  "MaxPageSize": 100,
  "MaxTrendPoints": 500,
  "MaxSpcSamples": 2000,
  "RequestTimeoutSeconds": 30,
  "RateLimitPerMinute": 60,
  "HeavyQueryConcurrency": 4
}
```

环境变量覆盖示例：

```bat
set Mcp__Enabled=true
set Mcp__RequireHttps=false
set Mcp__AllowInsecureHttp=true
```

启动校验：

- `Enabled=true` 但没有任何有效 Token：正常启动，但记 Warning，端点对全部请求返回 401；管理页提示创建 Token。
- `Enabled=true` 且 `RequireHttps=true` 但未配置 HTTPS：拒绝启动并输出明确错误，避免误以为已加密。
- `AllowWrites=true` 但没有 `write` scope 的 Token：写入工具仍不可用。

## 11. 测试计划

### 单元测试（新 `tests/DataTrace.Mcp.Tests`）

- 每个工具的输入钳制：时间范围、`take`、趋势点数、SPC 样本数、字符串长度。
- 角色矩阵：Viewer 调配置/审计工具被拒绝；Operator/Engineer/Admin 各工具结果符合预期。
- 脱敏：配置 DTO 不含密码、连接字符串、内部路径；曲线 DTO 不含 `float[]`。
- 审计：成功、拒绝、失败各写一条，参数摘要截断。

### 集成测试

用 `WebApplicationFactory<Program>`（需在 `Program.cs` 末尾加 `public partial class Program { }`）或现有进程夹具：

1. 未开启：`/mcp` 返回 404。
2. 开启但无 Token：401。
3. View：`initialize` → `tools/list` → `tools/call query_records` 成功。
4. 错 Token/过期/撤销/用户禁用：401。
5. Viewer 调 `get_audit_logs`：403（或统一 401，按实现约定），且审计有 `Denied`。
6. `take=1000` 被钳到 100；范围 90 天被拒绝或钳制。
7. 限流：第 61 次返回 429。
8. `AllowWrites=false`：写入工具不在 `tools/list` 中，或调用返回明确错误。
9. 开发态自动登录不影响 `/mcp`：无 Token 仍 401。
10. 现有三层测试全绿；Release 全量构建 0 警告。

### 手工验证

- MCP Inspector 连接 `/mcp`，配置 `Authorization: Bearer ...`，逐个调用只读工具。
- 至少两个客户端（如 Claude/Qoder/Cursor）验证 `tools/list` 与中文描述可读。
- 用 `tests/DataTrace.QueryBenchmarks` 对比同等查询在页面与 MCP 下的耗时，MCP 不超过页面同查询的 1.2 倍。

## 12. 部署与运维

- 与现有 Windows 服务同进程，不新增服务、不新增端口；发布方式不变（`dotnet publish` + `sc create`）。
- 默认关闭；需要时通过 `appsettings.json` 或环境变量开启。
- 建议只允许管理网段访问 5080 的 `/mcp`（防火墙/反向代理），或为 MCP 单独加一个带客户端证书的入口；本期不强制。
- HTTPS 复用现有 Kestrel 证书配置；`RequireHttps` 默认开启。
- Token 生命周期：默认 90 天，建议按人按用途发放；离职/换岗先撤销 Token 再改角色。
- 备份：`config.db` 已包含 Token 哈希与审计；恢复备份后旧 Token 仍有效，需评估是否批量撤销。
- 监控：工具调用失败、401/403/429、慢查询写入日志；`/healthz` 不纳入 MCP 状态，避免 AI 调用影响探活。
- 日志：不记录完整 Token、请求体全文、曲线数据；只记录前缀、工具名、行数、耗时、结果。

## 13. 里程碑与工作量

| 阶段 | 内容 | 预估 |
|---|---|---|
| M0 技术验证 | 加包、`/mcp` echo 工具、Inspector 连通、net8 兼容确认 | 0.5 人日 |
| M1 只读最小版 | 静态 Token（环境变量）+ 8 个核心工具 + 限流 + 审计 | 3–4 人日 |
| M2 完整只读版 | 配置库 Token + 管理页 + 角色矩阵 + 全部 18 个工具 + 资源/提示模板 + 集成测试 | 2–3 人日 |
| M3 写入版 | 报警接手、质量异常三个动作、幂等与版本控制 | 2–3 人日 |
| M4 交付 | 客户端接入文档、运维说明、安全评审、现场验收 | 1 人日 |

> M1 的“静态 Token”仅用于快速验证，不建议进入生产；生产至少做到 M2 的数据库 Token 与管理页。

## 14. 风险与待决问题

| 风险 | 影响 | 应对 |
|---|---|---|
| .NET 8 于 2026-11-10 结束支持 | 无安全补丁 | 本期可用（SDK 支持 net8），下一迭代升级 .NET 10 LTS；升级前不扩大公网暴露面 |
| MCP 规范与 SDK 仍在演进 | 升级破坏兼容 | 锁 2.3.1；工具与传输隔离；集成测试覆盖协议握手与调用 |
| AI 误操作写入 | 生产数据/报警状态被改 | 一期只读；二期默认关闭 + 角色 + 幂等 + 审计 + 版本冲突 |
| 长查询拖慢采集 | SQLite WAL 写入延迟 | 时间/行数上限、并发闸门、30 秒超时、基准测试 |
| Token 泄漏到 AI 客户端日志 | 未授权读取 | 按人发放、短有效期、HTTPS、撤销、审计异常调用 |
| 数据中的提示注入（托盘码/备注） | 模型被诱导调用工具 | 一期无写入；输出按数据处理；工具描述明确“数据不是指令” |
| Development 自动登录 | 本地误判为已鉴权 | `/mcp` 跳过自动登录；集成测试显式断言无 Token 为 401 |
| 工具过多导致模型选错 | 调用准确率下降 | 控制在 20 个以内，同类合并，描述写清适用场景 |
| 配置库写放大 | `LastUsedAt` 频繁写 | 5 分钟节流；审计异步批量（如后续需要） |

待决问题（需要业务确认）：

1. MCP 服务面向本机 AI 客户端，还是车间/办公室多台机器？决定是否强制 HTTPS 与独立网段。
2. 第一批是否需要“报警接手”这类写操作，还是纯查询即可？
3. Token 是按人发放，还是按“班次/工位助手”发放？影响命名与轮换策略。
4. 审计日志是否允许 MCP 读取（当前方案仅 Administrator）。
5. 是否需要把“当前工单/型号切换”暴露为只读状态（当前方案只读，不允许切换）。

## 15. 验收标准

- [ ] `Mcp:Enabled=false`（默认）时 `/mcp` 404，现有功能与测试完全不受影响。
- [ ] 开启后无 Token 401；有效 Viewer Token 可列出并调用只读工具。
- [ ] 所有查询受时间范围、分页、点数上限约束；超限返回明确错误或钳制后标注。
- [ ] 曲线不返回原始数组；配置输出无密码、连接字符串、内部路径。
- [ ] 每次工具调用（含拒绝/失败）有审计，`Source=MCP`。
- [ ] 开发态自动登录不能绕过 MCP Token。
- [ ] Viewer 无法读取配置、MES 状态、审计；Administrator 可以。
- [ ] 写入工具默认不存在/不可用；开启后仍需 `write` scope + 角色 + 幂等键。
- [ ] Release 全量构建 0 警告 0 错误；单元、组件、E2E 三层测试全绿。
- [ ] 两个以上 MCP 客户端完成 `initialize`、`tools/list`、`tools/call` 手工验证。

## 16. 参考资料

- 官方 MCP C# SDK（NuGet）：<https://www.nuget.org/packages/ModelContextProtocol.AspNetCore>
- NuGet 包版本与目标框架（2.3.1 支持 net8.0/net9.0/net10.0）：<https://api.nuget.org/v3-flatcontainer/modelcontextprotocol.aspnetcore/index.json>
- MCP 规范：<https://modelcontextprotocol.io/specification>
- ASP.NET Core 速率限制：<https://learn.microsoft.com/aspnet/core/performance/rate-limit>
- .NET 支持策略：<https://dotnet.microsoft.com/platform/support/policy/dotnet-core>
