# DataTrace 「装得快」运维脚本

面向 **一客户一套部署**：每个客户独立安装目录 + 独立 `data\config.db`，互不影响。

本目录为**运维脚本**，不改动应用核心业务默认值（如开发演示的 `SimulatorAutoRun` 等）。客户安装通过 `appsettings.Production.json` / `customer.json` 覆盖。

## 目录与脚本

| 脚本 | 作用 |
|---|---|
| `check-prereq.ps1` | 检查 .NET 8 ASP.NET Core Runtime / SDK |
| `publish.ps1` | `dotnet publish` DataTrace.Web（Release） |
| `install.ps1` | 安装/升级到 `D:\Apps\DataTrace\{CustomerId}\`，默认保留 `data\` |
| `start.ps1` / `stop.ps1` / `status.ps1` | 控制台进程启停与状态（写 `.datatrace.pid`） |
| `verify.ps1` | 自检：文件、可写、端口、HTTP、品牌配置 |
| `install-service.ps1` / `uninstall-service.ps1` | Windows 服务（需管理员；勿误停演示） |
| `set-firewall.ps1` | 可选：站内防火墙（需管理员） |
| `smoke-test.ps1` | 独立目录+备用端口端到端冒烟 |
| `CHECKLIST.md` | 开通验收清单 |
| `_common.ps1` | 内部共用函数（勿单独改业务配置） |

发布产物默认输出到 `deploy\publish\`（可参数覆盖）。

## 前提

1. 安装 [.NET 8](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)
   - **只跑已发布包**：ASP.NET Core 8.0 Runtime（推荐 Windows Hosting Bundle）
   - **本机需要编译发布**：安装 .NET 8+ SDK
2. 在仓库根目录有源码；脚本无需再 clone：

```powershell
cd D:\SourceCode\DataTrace\deploy
.\check-prereq.ps1
```

## 安装第一个客户

```powershell
cd D:\SourceCode\DataTrace\deploy

# 1) 发布
.\publish.ps1

# 2) 安装到 D:\Apps\DataTrace\CustomerA ，端口 5080
.\install.ps1 -CustomerId CustomerA -Port 5080 -SiteName "CustomerA一厂"

# 3) 启动（控制台后台进程）
.\start.ps1 -InstallDir D:\Apps\DataTrace\CustomerA

# 4) 自检
.\verify.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
```

浏览器打开：`http://127.0.0.1:5080/`

### 默认账号（仅首次无 config.db 时由种子创建）

| 用户 | 密码 | 角色 |
|---|---|---|
| admin | Admin@123 | 管理员 |
| engineer | Engineer@123 | 工程师 |
| operator | Operator@123 | 操作员 |
| viewer | Viewer@123 | 访客 |

**请首次登录后立即修改密码。** 脚本与文档从不把口令写回源码。

## 安装第二个客户（另一目录）

```powershell
.\publish.ps1   # 可复用上次 publish 产物
.\install.ps1 -CustomerId CustomerB -Port 5082 -SiteName "CustomerB二厂"
.\start.ps1 -InstallDir D:\Apps\DataTrace\CustomerB
.\verify.ps1 -InstallDir D:\Apps\DataTrace\CustomerB
```

要点：

- 目录：`D:\Apps\DataTrace\CustomerB\`（与 CustomerA 完全隔离）
- 数据：各自 `data\config.db`
- 端口：必须不同（5080 / 5081 / 5082…）
- 服务名（若注册服务）：`DataTrace-CustomerB`

## 配得开（客户品牌与现场安全默认）

一客户一套部署。安装脚本会写入：

| 项 | 位置 | 说明 |
|---|---|---|
| 厂名 SiteName | `customer.json` + `appsettings.Production.json` → `Customer:SiteName` | 顶栏 / 登录页 / 浏览器标题 |
| Logo | 安装目录 `branding\` + `LogoPath` | 如 `logo.png` → `/branding/logo.png` |
| 端口 | `Kestrel:Endpoints:Http:Url` + `customer.json` Port | `install.ps1 -Port` |
| 数据目录 | `DataRoot`（相对安装目录或绝对路径） | 默认 `data`；环境变量 `DataRoot` 可覆盖 |
| 环境 | `ASPNETCORE_ENVIRONMENT=Production` | `start.ps1` / 服务安装写入 |
| 仿真自动跑 | `Customer:SimulatorAutoRun=false` | **现场默认关**；开发演示（Development）仍默认开 |

```powershell
.\install.ps1 -CustomerId ACME -Port 5080 -SiteName "ACME一厂压装线"
# 将 logo.png 放到 D:\Apps\DataTrace\ACME\branding\
# 或在「系统设置」页（管理员）填写厂名 / Logo 文件名并「保存品牌」
```

多产线：每个产线一个 `InstallDir`（或未来 `SiteId`），不要共享同一个 `config.db`。

## 升得了（安全升级 / 回滚）

一客户一套部署：**不要为每个客户 fork 仓库**。统一源码 + `deploy\publish`，每客户独立 `InstallDir` + `data\config.db`。

### 版本号

- 源码统一版本：仓库根 `Directory.Build.props` 的 `<Version>` / `<InformationalVersion>`（当前 **1.1.0**）
- 可见位置：系统设置页「应用版本」；安装目录 `version.json` 与 `customer.json` 的 `AppVersion` / `PreviousVersion` / `UpgradedAt`
- 升版流程：改 `Directory.Build.props` → `.\publish.ps1` → 对各客户 `.\upgrade.ps1`

### 客户现场升级（推荐）

```powershell
cd D:\SourceCode\DataTrace\deploy

# 1) 发布新版本（可先在本机完成）
.\publish.ps1

# 2) 升级指定客户（自动：停本实例 → 备份 → 换程序 → 写版本 → 启动 → 自检）
.\upgrade.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
# 或: .\upgrade.ps1 -CustomerId CustomerA

# 已有 publish 产物、跳过编译：
.\upgrade.ps1 -CustomerId CustomerA -SkipPublish
```

升级行为要点：

| 步骤 | 说明 |
|---|---|
| 停止 | 只停本 `InstallDir`（PID 文件 / 本目录 exe），**不杀其它端口**（如演示 5080） |
| 备份 | `InstallDir\backups\upgrade-yyyyMMdd-HHmmss\`：含 `data\`、`customer.json`、`binaries\` |
| 换程序 | robocopy 覆盖程序文件；**保留** `data\`、`branding\`、`customer.json`、`appsettings.Production.json` |
| 库结构 | 与正常启动相同：`DatabaseSeeder` 的 `EnsureCreated` + `SqliteSchema.AddColumnIfMissing`（无独立 EF Migrate） |
| 版本 | 写入 `version.json` + `customer.json` 的 AppVersion |

等价手工路径（旧文档，仍可用；优先用 `upgrade.ps1`）：

```powershell
.\stop.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
.\publish.ps1
.\install.ps1 -CustomerId CustomerA -Port 5080 -SiteName "CustomerA一厂"
# 默认不覆盖 data\；不要加 -ForceData
.\start.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
.\verify.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
```

### 仅触发库补丁（migrate.ps1）

```powershell
.\migrate.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
```

实质是重启实例，让启动种子跑完补列；**没有**单独 migrate-only Host，也**不需要**手工 SQL。

### 回滚

```powershell
# 列出备份
.\rollback.ps1 -InstallDir D:\Apps\DataTrace\CustomerA -List

# 只回程序（常见：列补丁向前兼容时够用）
.\rollback.ps1 -InstallDir D:\Apps\DataTrace\CustomerA `
  -BackupDir D:\Apps\DataTrace\CustomerA\backups\upgrade-20260924-120000

# 程序 + 数据一起回（更稳妥；会先把当前 data 改名为 data-bad-...）
.\rollback.ps1 -InstallDir D:\Apps\DataTrace\CustomerA `
  -BackupDir D:\Apps\DataTrace\CustomerA\backups\upgrade-20260924-120000 -RestoreData
```

**说明**：当前库变更是**向前加列**，一般回程序即可；若未来出现破坏性迁移，必须 **binaries + data 成套回滚**。禁止在未备份时对生产使用 `-ForceData`。

### Soft 许可提示（可选）

`customer.json` 可写 `MaxStations` / `MaxPlcs`（整数）。系统设置页在超限时**仅警告**，不阻断配置、不是 DRM。

---
## Windows 服务（可选）

```powershell
# 管理员 PowerShell
.\install-service.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
Start-Service -Name DataTrace-CustomerA

# 卸载服务（不删 data）
.\uninstall-service.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
```

非管理员环境会**失败并给出明确说明**；可继续用 `start.ps1`。

## 端口与环境说明

- 安装时写入 `appsettings.Production.json`（`Kestrel:Endpoints:Http:Url`）
- `start.ps1` / 服务还设置环境变量（双重保险）：
  - `ASPNETCORE_ENVIRONMENT=Production`
  - `Kestrel__Endpoints__Http__Url=http://0.0.0.0:{端口}`
  - `DataRoot=data`（或 customer.json 中的 DataRoot）
- 应用 ContentRoot 固定为 exe 所在目录，数据在安装目录的 `data\`

## 冒烟测试（不打扰演示 5080）

```powershell
.\smoke-test.ps1
# 默认安装到 D:\Apps\DataTrace\_smoke-test ，端口 5081，结束后自动停止

# 配得开冒烟示例：
.\install.ps1 -CustomerId _cfg-smoke -Port 5082 -SiteName "配得开冒烟厂"
.\start.ps1 -InstallDir D:\Apps\DataTrace\_cfg-smoke -Replace
.\verify.ps1 -InstallDir D:\Apps\DataTrace\_cfg-smoke
.\stop.ps1 -InstallDir D:\Apps\DataTrace\_cfg-smoke -Force
```

## 安全约束

- 发布与安装不清洗仓库内或演示实例的 `data\` / `config.db`
- `verify` / `smoke-test` 使用独立目录与备用端口；默认不 `-Replace` 杀其它进程
- `dotnet publish` 失败则中止，不会半安装

## 开通现场

见 [CHECKLIST.md](./CHECKLIST.md) 逐项勾选：PLC 型号、点位、工站、配方、账号、备份。

## 数据库备份与恢复

应用内默认启用定时在线备份（`Backup` 配置节，可用 `customer.json` / `appsettings.Production.json` 覆盖）：

| 键 | 默认 | 说明 |
|---|---|---|
| `Backup:Enabled` | `true` | 是否启用定时备份 |
| `Backup:DailyTime` | `02:30` | 本地每日备份时间 |
| `Backup:BackupDirectory` | 空 → `{DataRoot}/backups` | 备份根目录 |
| `Backup:RetentionDays` | `30` | 按天保留 |
| `Backup:MaxBackups` | `60` | 最多保留套数（0=不限数量） |

采集记录与曲线文件的长期保留由系统设置页「保留年数」控制（配置库 `SystemSettings.RetentionYears`），由 `RetentionHostedService` 约每 6 小时删除超过年限的整月 `runtime/data_yyyyMM.db` 及对应曲线目录；与备份套数保留（`Backup:RetentionDays` / `MaxBackups`）是两套机制，互不替代。已废弃的 `Backup:RecordRetention` 配置键若仍存在会被忽略。

- 备份内容：`config.db`（含 Identity）+ `runtime/data_yyyyMM.db`。使用 SQLite Online Backup API，**不要**对正在运行的库做裸文件拷贝。
- 每套备份目录形如 `data/backups/2026-09-24_0230/`，内含 `manifest.json`（版本、时间、大小、SHA256、quick_check）。
- **强烈建议**把 `backups` 目录定期拷到其它磁盘或 NAS；本机同盘不能防磁盘损坏。
- 设置页「数据库备份」卡片可查看状态；管理员可「立即备份」。
- `deploy/backup-now.ps1`：维护窗口离线备份（可选 `-StopApp`）；运行中优先用设置页按钮。
- `deploy/restore.ps1 -InstallDir ... -Latest`（或 `-BackupSet`）：停应用 → 当前库挪到 `pre-restore-*` → 恢复 → 启动并 verify。
- `upgrade.ps1` 仍使用升级专用 `backups/upgrade-*` 整目录快照（含二进制）；与每日库备份互补，未强行合并。

