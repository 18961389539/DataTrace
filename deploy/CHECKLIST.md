# DataTrace 开通验收清单（一客户一套部署）

> 每个客户独立安装目录 + 独立 `data\config.db`。开通前请逐项确认。

## 1. 环境与安装

| 项 | 状态 | 备注 |
|---|---|---|
| 本机跑 `deploy\check-prereq.ps1`，.NET 8 ASP.NET Core 通过 | ☐ | 缺则装 Hosting Bundle / SDK |
| 已 `publish.ps1` 与 `install.ps1 -CustomerId <客户>` 成功 | ☐ | 安装目录形如 `D:\Apps\DataTrace\<客户>` |
| `verify.ps1` 全部 PASS | ☐ | |
| 端口已规划且不与其它客户/演示冲突 | ☐ | 默认 5080；第二客户用 5081/5082 |
| （可选）管理员执行 `set-firewall.ps1` | ☐ | 需局域网访问时 |
| （可选）管理员执行 `install-service.ps1` | ☐ | 否则用 `start.ps1` 控制台进程 |

## 2. 默认账号（首次安装自动创建）

| 用户 | 初始密码 | 角色 |
|---|---|---|
| admin | Admin@123 | 管理员 |
| engineer | Engineer@123 | 工程师 |
| operator | Operator@123 | 操作员 |
| viewer | Viewer@123 | 访客 |

- [ ] **首次登录后立即修改 admin（及其它角色）密码**
- [ ] 运行环境确认为 `ASPNETCORE_ENVIRONMENT=Production`（`start.ps1` / 服务脚本会设置），勿开开发态免登录

## 3. 配得开（品牌 / 演示关闭）

- [ ] `customer.json` 含 CustomerId、SiteName、Port、DataRoot、Environment=Production、SimulatorAutoRun=false
- [ ] `appsettings.Production.json` 含 Customer.SiteName 与 Customer.SimulatorAutoRun=false
- [ ] 安装目录存在 `branding\`；如需 Logo 已放入并配置 LogoPath
- [ ] 顶栏 / 登录页显示客户厂名
- [ ] 现场未误开「启动后自动跑仿真」（系统设置页只读可见；改动在 PLC 仿真页）
- [ ] 多产线采用多安装目录，数据互不影响

## 4. PLC / 点位 / 工站

| 项 | 填写 / 确认 |
|---|---|
| PLC 型号 / 品牌 | 西门子 S7 / 三菱 MC / 欧姆龙 FINS / Modbus / 模拟… |
| IP / 端口 / 机架槽位 | |
| 点位表（版本号/文件名） | |
| 工站清单（首站/末站与顺序） | |
| 托盘码地址与长度 | |
| 触发地址与触发值 | |
| 曲线点位（位移/压力） | |
| 产品型号 / 配方限值 | |

> 新客户若无真实 PLC：可在「PLC 连接」把演示模拟连接改为真实参数，并关闭模拟自动跑线。

## 5. 功能冒烟（建议）

| 项 | 状态 |
|---|---|
| 登录 / 角色权限页可见性正确 | ☐ |
| PLC 连接状态 / 心跳正常 | ☐ |
| 采集 → 实时看板有记录 | ☐ |
| 数据可查询与导出 | ☐ |
| 配方限值判定与点位一致 | ☐ |
| 备份/恢复 `data\` 演练过 | ☐ |
| 局域网可打开 `http://<服务器IP>:<端口>/` | ☐ |

## 6. 运维注意

- 升级只覆盖程序文件，**默认保留 `data\`**（含 `config.db`、runtime、curves、logs）
- 不要对生产客户使用 `-ForceData`，除非已备份并明确要重建库
- 详细命令见 `README.md`
- **不要改业务默认值**（如开发演示的 `SimulatorAutoRun`）；客户现场靠 Production 配置默认关模拟自动跑

## 7. 备份、升级与回滚（升得了）

| 项 | 说明 |
|---|---|
| 数据备份 | 拷贝整个 `data\`（至少 `config.db`、`runtime\`、`curves\`）；升级脚本也会自动备份到 `backups\upgrade-*` |
| 升级命令 | `.\upgrade.ps1 -InstallDir D:\Apps\DataTrace\<客户>`（先 `publish.ps1`；勿 `-ForceData`） |
| 版本可见 | 系统设置「应用版本」+ `version.json` / `customer.json` 的 AppVersion |
| 库结构 | 随应用启动补列（EnsureCreated + AddColumnIfMissing）；可用 `migrate.ps1` 仅重启触发 |
| 回滚程序 | `.\rollback.ps1 -InstallDir ... -BackupDir ...\backups\upgrade-...` |
| 回滚数据 | 同上加 `-RestoreData`（先把坏 data 改名另存） |
| 禁止 | 未备份时对生产使用 `-ForceData`；不要为每客户 fork 仓库 |

- [ ] 已演练一次升级/回滚（可用 `_upg-smoke` 独立目录 + 备用端口）
- [ ] 已记录本客户 AppVersion

## 8. 开通签字

| 角色 | 签字 | 日期 |
|---|---|---|
| 实施 | | |
| 客户确认 | | |