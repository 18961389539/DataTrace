# DataTrace 「装得快」运维脚本

面向 **一客户一套部署**：每个客户独立安装目录 + 独立 `data\config.db`，互不影响。

本目录为**运维脚本**，不改动应用核心业务默认值（如开发演示的 `SimulatorAutoRun` 等）。客户安装通过 `appsettings.Production.json` / `customer.json` 覆盖。

## 目录与脚本

| 脚本                                              | 作用                                                    |
| ----------------------------------------------- | ----------------------------------------------------- |
| `check-prereq.ps1`                              | 检查 .NET 8 ASP.NET Core Runtime / SDK                  |
| `publish.ps1`                                   | `dotnet publish` DataTrace.Web（Release）               |
| `install.ps1`                                   | 安装/升级到 `D:\Apps\DataTrace\{CustomerId}\`，默认保留 `data\` |
| `start.ps1` / `stop.ps1` / `status.ps1`         | 控制台进程启停与状态（写 `.datatrace.pid`）                        |
| `verify.ps1`                                    | 自检：文件、可写、端口、HTTP、品牌配置                                 |
| `install-service.ps1` / `uninstall-service.ps1` | Windows 服务（需管理员；勿误停演示）                                |
| `set-firewall.ps1`                              | 可选：站内防火墙（需管理员）                                        |
| `smoke-test.ps1`                                | 独立目录+备用端口端到端冒烟                                        |
| `CHECKLIST.md`                                  | 开通验收清单                                                |
| `_common.ps1`                                   | 内部共用函数（勿单独改业务配置）                                      |

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

### 首次管理员账号

Production 首次初始化只创建 `admin` 管理员，不创建固定演示账号。未设置 `Seed__AdminPassword` 时，应用生成一次性随机口令并写入 `{DataRoot}\logs\datatrace-*.log`；也可在首次启动前通过受控环境变量 `Seed__AdminPassword` 提供引导口令。两种方式都会要求首次登录立即改密。请由授权运维人员从本机日志或安全交付渠道取得口令，并在交付后按现场策略处理临时凭据。

`admin/Admin@123` 等固定账号只用于 Development / 明确启用演示账号的 Staging 与自动化测试，不适用于 Production。

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

| 项           | 位置                                                                    | 说明                                  |
| ----------- | --------------------------------------------------------------------- | ----------------------------------- |
| 厂名 SiteName | `customer.json` + `appsettings.Production.json` → `Customer:SiteName` | 顶栏 / 登录页 / 浏览器标题                    |
| Logo        | 安装目录 `branding\` + `LogoPath`                                         | 如 `logo.png` → `/branding/logo.png` |
| 端口          | `Kestrel:Endpoints:Http:Url` + `customer.json` Port                   | `install.ps1 -Port`                 |
| 数据目录        | `DataRoot`（相对安装目录或绝对路径）                                               | 默认 `data`；控制台与 Windows 服务均读取客户配置       |
| 环境          | `ASPNETCORE_ENVIRONMENT=Production`                                   | `start.ps1` / 服务安装写入                |
| 仿真自动跑       | `Customer:SimulatorAutoRun=false`                                     | **现场默认关**；开发演示（Development）仍默认开     |

```powershell
.\install.ps1 -CustomerId ACME -Port 5080 -SiteName "ACME一厂压装线"
# 将 logo.png 放到 D:\Apps\DataTrace\ACME\branding\
# 或在「系统设置」页（管理员）填写厂名 / Logo 文件名并「保存品牌」
```

多产线：每个产线一个 `InstallDir`（或未来 `SiteId`），不要共享同一个 `config.db`。

## 升得了（安全升级 / 回滚）

### CI 隔离验收与备份停顿测量

`acceptance-isolated.ps1 -PublishDir <发布目录>` 在系统临时目录创建唯一安装，并只使用本机随机空闲端口与合成数据。它复用 `install.ps1`、`start.ps1`、`verify.ps1`、`upgrade.ps1`、`backup-now.ps1`、`verify-backup.ps1` 和 `rollback.ps1`，验证安装、升级保留 DataRoot、全量备份校验和带数据回滚。脚本结束会停止实例并删除本轮唯一临时目录；不读取客户数据。

验收会写入 8 MiB 随机归档文件，并以 `backup-now.ps1 -StopApp` 实测停机、备份、重启总时长，输出 `BACKUP_STOP_START_SECONDS`。这是该合成规模的离线维护窗口参考值，不代表在线备份锁住采集的精确时长。扩展容量设计时，应在隔离环境用与计划数据规模相同的合成文件集重复测量，记录总字节数、文件数、SQLite 数量、备份时长和采集停顿；不要仅按 8 MiB 线性外推。

`DatabaseBackupTests` 中的 `Synthetic_backup_records_real_maintenance_and_waiting_read_durations` 则调用真实 `DatabaseBackupService` 和维护屏障：它在独占维护锁期间计时等待读 lease 的请求，并输出 JSON（`payloadBytes`、`payloadFiles`、`sqliteDatabases`、`backupElapsedMilliseconds`、`maintenanceExclusiveMilliseconds`、`waitingReadLeaseMilliseconds`）。本机 8 MiB / 4 文件 / 1 个配置库的一次合成实测为备份 158.8 ms、独占维护 154.5 ms、等待读 lease 140.1 ms；这只代表当前机器与该小样本。可用 `DATATRACE_BACKUP_BENCHMARK_BYTES`（1–536870912）和 `DATATRACE_BACKUP_BENCHMARK_FILES`（1–1000）设置工作负载，并用 `DATATRACE_BACKUP_BENCHMARK_OUTPUT` 保存结果。容量评估应在无客户数据的隔离环境按目标文件数、文件总量与数据库数量分档实际运行（例如 64 MiB/16 文件、256 MiB/64 文件，再按计划规模补测），报告每档原始 JSON、备份独占时长和采集等待；不得把小样本线性推算成大规模性能结论。

一客户一套部署：**不要为每个客户 fork 仓库**。统一源码 + `deploy\publish`，每客户独立 `InstallDir` + `data\config.db`。

### 版本号

- 源码统一版本：仓库根 `Directory.Build.props` 的 `<Version>`（当前 **1.1.0**）
- **构建标识**：产物里烘焙了提交号（形如 `1.1.0+d279acf`，取值顺序 GITHUB_SHA → 直接读 `.git` → `nogit`），
  用来回答"这台机器跑的是哪次提交"——同一个版本号下的不同提交也能分辨
- 可见位置：系统设置页「应用版本」+「构建标识」；安装目录 `version.json` 的 `AppVersion` / `BuildStamp` / `PreviousVersion` / `UpgradedAt`，
  以及 `customer.json` 的 `AppVersion` / `BuildCommit`
- `verify.ps1` 会比对 exe 里的构建标识与 `version.json` 记录的那一份：不一致说明程序文件被换过（手工拷过、装错包、回滚没走脚本）
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

| 步骤  | 说明                                                                                             |
| --- | ---------------------------------------------------------------------------------------------- |
| 停止  | 只停本 `InstallDir`（PID 文件 / 本目录 exe），**不杀其它端口**（如演示 5080）                                        |
| 备份  | `InstallDir\backups\upgrade-yyyyMMdd-HHmmss\`：含实际配置 DataRoot 的完整副本（归档在备份集 `data\`）、来源路径清单、`customer.json`、`binaries\`            |
| 换程序 | robocopy 覆盖程序文件；**保留** `data\`、`branding\`、`customer.json`、`appsettings.Production.json`       |
| 库结构 | 与正常启动相同：`DatabaseSeeder` 的 `EnsureCreated` + `SqliteSchema.AddColumnIfMissing`（无独立 EF Migrate） |
| 版本  | 写入 `version.json` + `customer.json` 的 AppVersion                                               |

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

**说明**：只回程序前，应确认目标旧程序能够读取当前数据库；本次待确认记录的索引也发生了变更，不能仅凭“加列”推断兼容。若选择 `-RestoreData`，脚本按升级备份清单核对原 DataRoot 路径，并把完整数据集恢复回该路径。当前数据先改名为同级带时间戳和随机后缀的 `*-bad-*`，恢复来源与目标写入 `rollback-restore-manifest.json`。不认识 DataRoot 的旧备份会拒绝恢复，不会退回安装目录下的 `data\` 假设。破坏性或不兼容迁移必须 **binaries + data 成套回滚**。禁止在未备份时对生产使用 `-ForceData`。

升级备份、程序覆盖和程序回滚都排除实际 DataRoot；相对路径的自定义数据目录同样不会混入二进制副本。复制最多重试两次、每次等待一秒；数据恢复复制失败时保留部分恢复目录并放回恢复前的数据目录。DataRoot 不允许等于安装目录或包含安装目录，备份来源也不能位于恢复目标内部。

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
  - `DataRoot`（`customer.json` 中配置的相对或绝对路径）
- 应用 ContentRoot 固定为 exe 所在目录；未配置时数据默认在安装目录的 `data\`。外置 DataRoot 会由控制台启动和服务注册共同使用。

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

| 键                        | 默认                       | 说明             |
| ------------------------ | ------------------------ | -------------- |
| `Backup:Enabled`         | `true`                   | 是否启用定时备份       |
| `Backup:DailyTime`       | `02:30`                  | 本地每日备份时间       |
| `Backup:BackupDirectory` | 空 → `{DataRoot}/backups` | 备份根目录          |
| `Backup:RetentionDays`   | `30`                     | 按天保留           |
| `Backup:MaxBackups`      | `60`                     | 最多保留套数（0=不限数量） |

采集记录与曲线文件的长期保留由系统设置页「保留年数」控制（配置库 `SystemSettings.RetentionYears`）。`RetentionHostedService` 约每 6 小时检查旧月库；含较新记录、未闭合会话、未决确认、在制索引、MES 意图或 spool 的月份会保留。清理文件前还会检查其他保留月库的跨月引用。与备份套数保留（`Backup:RetentionDays` / `MaxBackups`）是两套机制，互不替代。已废弃的 `Backup:RecordRetention` 配置键若仍存在会被忽略。

- 全量数据范围：`config.db`（含 Identity）、`runtime/` 月库及其它文件、`curves/`、`archive/`、`audit-archive/`、`spool/`。数据库使用 SQLite Online Backup API；其它文件逐项记录大小和 SHA-256。备份目录本身不递归纳入。
- 在线备份获取独占维护锁，等待正在执行的采集、补传和出站转移完成，并在快照期间阻止新的业务写入与保留清理；较大的数据集会相应延长采集等待，应在维护窗口执行并实测节拍影响。
- 每套备份目录形如 `data/backups/2026-09-24_0230/`，只在完整清单写入后发布；`manifest.json` 的 `formatVersion=2`、`backupType=full-data`、`complete=true` 表明这是可验证的全量数据集。
- **不包含**应用程序二进制、安装目录的 `customer.json` / `appsettings.Production.json` 和运行日志；这些部署资产应按客户运维策略单独备份。
- **强烈建议**把 `backups` 目录定期拷到其它磁盘或 NAS；本机同盘不能防磁盘损坏。
- 设置页「数据与备份」显示数据库数、文件校验数和快照大小；管理员可「立即全量备份」。
- `deploy/verify-backup.ps1 -InstallDir ... -Latest`（或 `-BackupSet`）可只读验证清单、每个文件的大小/SHA-256 和 SQLite `quick_check`，不会停止应用或改写数据；请使用与安装包位数匹配的 PowerShell，脚本通过应用目录内的原生 SQLite 库执行校验。
- `deploy/backup-now.ps1`：维护窗口离线全量备份（可选 `-StopApp`）；运行中优先用设置页按钮。
- `deploy/restore.ps1 -InstallDir ... -Latest`（或 `-BackupSet`）：先完整预检；通过后停应用，生成并验证当前数据的 `pre-restore-*` 全量回滚点，再暂存和切换目标数据；启动前对现场文件集合、大小、SHA-256 和 SQLite `quick_check` 再验证，通过后才启动并做服务检查。失败时尝试回滚并重启原数据。
- 旧版数据库专用备份（manifest v2 之前）不满足全量恢复要求，`restore.ps1` 会拒绝使用；不要删除旧备份，必要时先在隔离目录由运维人员按数据库专用流程恢复。
- `upgrade.ps1` 仍使用升级专用 `backups/upgrade-*` 整目录快照（含二进制）；与每日库备份互补，未强行合并。
