# DataTrace 全面工程质量复核（2026-10-11，当前工作区）

**结论：已有较好的分层、测试和诊断基础，但仍不具备按 L3「现场可靠」验收的证据。最关键的缺口是回写恢复的事件身份、恢复阻塞状态、完整判定版本、跨库备份一致性和长连接撤权。** 单元测试通过不能替代这些故障边界的验证。不沿用旧报告的约 70 分：没有统一权重、测量基线和现场指标，百分制会掩盖关键阻塞项。

审查采用用户提供的十二项特性和自定义 L1–L5 模型，不作为正式认证。真实项目是 .NET 8 Blazor Server 的 PLC 数据采集、曲线判定和追溯系统；当前没有相机工作流编辑器和机器人调度，不把这些未声明能力计作缺陷。

## 1. 范围和证据

- Git 基线 `eca62c9a6c4b9497feead894e5c801d963de1994`，审查对象包括全部未提交修改与新增实现。
- 检查 Domain / Application / Plc / Collector / Infrastructure / Web，以及 CI、迁移、安装、升级、备份、恢复和回滚脚本。旧报告仅作为待核对问题清单。
- 本轮只新增报告和隔离验证产物，没有修改业务实现或正式测试。模拟器探针复用正式测试的环境初始化，执行真实采集管线、SQLite 仓储和 PLC 模拟驱动。
- 审查期间工作区有并行改动。较早核心、组件和 E2E 测试对应当时产物；后续停机代码不能直接继承这些通过结论。关键代码副本与 SHA256 保存于 [证据目录](D:/code/DataTrace/artifacts/quality-current/source-hashes.json)。报告末尾记录最终构建复核。
- 没有接真实 PLC，没有操作客户数据库，没有实施生产升级/恢复、真实断电、72 小时长稳、渗透或容量压测。

证据产物： [核心 TRX](D:/code/DataTrace/artifacts/quality-current/core.trx)、[组件 TRX](D:/code/DataTrace/artifacts/quality-current/web.trx)、[E2E TRX](D:/code/DataTrace/artifacts/quality-current/e2e.trx)、[隔离探针](D:/code/DataTrace/artifacts/quality-current/probe/Program.cs)、[探针结果](D:/code/DataTrace/artifacts/quality-current-probe.log)。

| 验证 | 本轮结果 | 解释 |
|---|---|---|
| 原始 Release 构建 | 54 警告、1 错误 | 本机 NuGet buildTransitive TFM 检查报 System.Collections.Immutable 不支持 net8.0；报文同时要求 netcoreapp3.1 或更高，需在干净缓存复核导入链，不能据此认定 net8 真不兼容 |
| 核心测试 | 1391/1391 通过 | 临时命令参数 `-p:SuppressTfmSupportBuildWarnings=true`，未修改仓库门禁 |
| Web 组件测试 | 346/346 通过 | 同上 |
| 真浏览器全量 | 112/118 通过，6 失败 | 独立 DataRoot / localhost，Release 应用，Edge，golden=auto，未更新视觉基线；Development 与 Staging 测试实例能够启动 |
| 末站回写失败恢复 | 复现重复采集 | 6 条记录 → 删除 pending → 下一轮 7 条记录 |
| 回写重试时设备换托盘 | 复现旧结果确认新件 | 新托盘触发从 1 写为 2，但新托盘记录数为 0 |
| 快照结构异常 | 复现异常和错误接受 | null limits / 重复 tagId 抛异常；unknown kind 被接受为 Frozen |
| EF 模型检查 | Config、Runtime 均无 pending model changes | 新迁移和 snapshot 已补齐；不是旧报告中的未修状态 |

探针可重跑：`dotnet run --project artifacts/quality-current/probe/Probe.csproj -c Release -p:SuppressTfmSupportBuildWarnings=true`。首次探针运行为避开审查中的源码中间态，额外用 `-p:BuildProjectReferences=false` 调用了刚才成功构建的项目产物；因此不能用它证明后续全部源码通过构建。其报告中的回写与快照路径已另行静态核对。

## 2. 十二项质量判断

「待验收」表示没有达到可验证承诺，不表示已在现场发生故障。关键 P1 不以其他维度优势抵消。

| 特性 | 已具备 | 主要缺口 / 证据 | 判断 |
|---|---|---|---|
| 1 功能正确性 | 地址编解码、规格判定、报表、跨月查询、模拟器；1391 核心测试 | 末站重复采集、旧结果确认新托盘（Q01/Q02）、快照规则边界（Q06） | 正常路径有证据；异常边界不通过 |
| 2 稳定性 | 单实例、串行请求、重连冷却、日志封顶、限次回写；新增停机汇合 | 任务登记竞态、停机等待不完整、无界积压（Q04/Q12） | 长期稳定性待验收 |
| 3 易用性 | 帮助、试读快照、字段定位、危险确认、触屏流程、查询方案 | 人工核对提示未形成持久阻塞及可验证恢复入口；2 个交互 E2E 超时 | 常规交互较好；故障操作闭环未完成 |
| 4 可维护性 | 多工程分层、接口、纯规则、职责拆分、集中包版本 | scoped 写 DbContext 长连接生命周期；配置提交前内存副作用；错误类型语义混用 | 较好基础，事务与生命周期边界需治理 |
| 5 可观测性 | 采集作用域、流水号、诊断页、通信流水、报警、healthz | 主循环心跳不代表业务就绪；数据库失败仍可能 Idle；恢复状态不完整（Q10） | 有工具，缺可信的业务状态 |
| 6 可恢复性 | 事务内 pending 和索引意图、spool 原子文件、幂等补传、恢复脚本 | 回写身份/销账、对账门禁、备份一致性、外置 DataRoot（Q01–Q03/Q05/Q07/Q09） | L3 核心阻塞 |
| 7 性能 | 合并读块、读计划缓存、曲线压缩、批量出站 | 顺序扫描故障耦合，pending 每次命中跨所有月库查询，补传整队读入；无 P95/P99 | 有优化，缺容量与节拍证据 |
| 8 可测试性 | 核心、bUnit、真实浏览器、协议 loopback、TRX/trace | 缺设备换件、末站恢复、任务竞态、进程硬退出、备份并发和撤权回归；6 个 E2E 失败 | 局部具 L4 能力，整体门禁未全绿 |
| 9 可扩展性 | PLC 工厂/能力契约、驱动适配、纯判定规则、仓储接口 | 新协议需同时验证规划/地址/错误契约；快照无显式 schemaVersion，未知 kind 不拒绝 | 模块基础可用，兼容性治理待补 |
| 10 安全性 | 角色、登录锁定、强制改密、危险操作确认、来源检查 | 已打开 Blazor 电路缺重新验证；操作服务缺最新权限校验（Q08）；默认 HTTP 依赖客户部署 | 基础防护具备，撤权生命周期不通过 |
| 11 数据可靠性 | 月库事务、MES 同事务意图与稳定幂等键、曲线 CRC、备份 SHA256、审计事务 | 多文件业务快照、保留删除引用、完整配置/算法修订链（Q07/Q13/Q14） | 单库较强，跨对象一致性不足 |
| 12 可部署性 | .NET 8 SDK 固定、Windows 服务、版本盖章、升级/回滚/恢复脚本、EF 迁移 | 原始构建未过；sln 改名遗漏视觉基线路径；外置数据回滚未覆盖；运维账号文档过时 | 可交付基础存在，交付门禁不通过 |

## 3. 具体问题与验收条件

严重度：P1 = 上线前关闭的正确性、恢复、权限或稳定性风险；P2 = 必须安排的工程缺口。没有足够证据宣布 P0。

### Q01 · P1 · 正常关闭的末站会话被当作无效留证，下一轮重复采集

**真实管线探针确认。** [重试判断](D:/code/DataTrace/src/DataTrace.Collector/StationCollectPipeline.cs:203) 对任何非 Open 会话删除 pending；但 [末站提交](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/RuntimeStore.cs:157) 正常关闭会话。业务关闭与设备确认是两个独立状态，末站恰好会命中这个分支。

模拟末站写失败：6 条记录、trigger=1、pending 存在。恢复写能力并重试后：confirmed=false、pending 被删、trigger 仍为 1。按 [下一轮扫描](D:/code/DataTrace/src/DataTrace.Collector/CollectionHostedService.cs:530) 的无 pending 路径执行真实管线，记录变成 7 条并写回流程异常码 8。人工关闭分支同样只是短暂显示 Fault，没有持久禁采状态。

修复：分别管理业务会话终结和设备确认；正常末站关闭仍允许身份核对后确认。需要人工核对时保留阻塞事件，必须完成核对才能解除，不能删除防重证据后让扫描恢复采集。验收覆盖末站/单站线/孤儿件、人工终结、重启及已写回但未销账。

### Q02 · P1 · 数据库会话状态与 24 小时时限不能证明设备仍是原件

**真实管线探针确认。** [确认重试](D:/code/DataTrace/src/DataTrace.Collector/StationCollectPipeline.cs:186) 只查数据库会话和 pending 年龄，随后用当前 station/connection 写旧结果，未读当前托盘码/事件号。留证也没有原 PLC 连接、地址、协议和配置版本。

首站 POLD01 写失败后，模拟器加载 PNEW01，数据库旧会话仍 Open。重试返回 true，新件触发写成 Success=2，新件记录为 0。读回结果码只证明寄存器写成功，不能证明确认了正确事件。

修复：设备提供稳定递增事件号，持久化并校验 `(device, eventId, address/protocol version)`；托盘码核对可作辅助，不能区分同托盘下一周期。无可靠事件身份时保持人工核对阻塞。验收覆盖换托盘、同托盘复用、配置改地址、PLC 重启和迟到响应。

### Q03 · P1 · spool 数据持久化与设备确认仍缺统一状态

**静态确认。** [写库失败路径](D:/code/DataTrace/src/DataTrace.Collector/CollectSessionCoordinator.cs:261) 把请求写 spool，再返回 DatabaseWriteFailed；此时月库事务没有 pending。若回写也失败，扫描只查月库 pending，不查 spool 事件，会再次采集。即使已有 spool 意图，[补传](D:/code/DataTrace/src/DataTrace.Collector/SpoolReplayRunner.cs:116) 无条件清除 RequiresWriteBack，假定历史件设备已放行，无法代表未知设备确认状态。

修复：本地持久事件应同时表示数据写入和设备确认状态；月库故障时也能阻止重采。补传不得把未知确认状态当作已确认。验收：月库拒写而 spool 可写 + PLC 回写失败 + 重启，数据只产生一次、确认状态仍可恢复。

### Q04 · P1 · 新增在途任务表存在完成先于登记的竞态，停机时限也未覆盖所有等待

**静态确认，尚未做全宿主竞态注入。** [任务启动与登记](D:/code/DataTrace/src/DataTrace.Collector/CollectionHostedService.cs:547) 为 TryAdd(null) → Task.Run → `_busy[id]=task`，任务 finally 则 TryRemove。如果任务快速完成并移除占位后，调度线程再赋值，会将已完成任务永久放回 busy；该工站后续一直跳过。确认和采集两条路径都有此模式。

[StopAsync](D:/code/DataTrace/src/DataTrace.Collector/CollectionHostedService.cs:678) 新顺序有进步，但 `base.StopAsync(CancellationToken.None)` 可无限等待扫描，队列 Dispose 同样等待同步驱动；10 秒只包住中间 WhenAll。所有工作还共享停止令牌：停止后新调度 Task.Run 若根本没运行，finally 不会解除占位。

修复：把任务身份登记与执行释放设计为原子生命周期；仅移除当前任务的条目，避免迟到完成影响下一任务；区分停止发起与有限收尾，整个停止过程服从宿主截止时间。验收用可控调度器覆盖完成早于登记、取消早于执行、队列卡住与安全收尾。

### Q05 · P1 · 启动对账只处理一批且失败后继续扫描，仍不是恢复就绪屏障

**静态确认。** [启动调用](D:/code/DataTrace/src/DataTrace.Collector/CollectionHostedService.cs:107) 只执行 RelayPendingAsync(200)。超过一批的意图、Deferred 坏载荷或对账异常均不阻止采集，错误仅记日志；后台 15 秒兜底不能保证第一件运行时索引已一致。

修复：明确全部排空/分类和业务 readiness；必要恢复失败时按受影响工站/托盘阻塞。验收覆盖 201 条以上未决意图、坏载荷队首、config.db 不可写、采集与对账并发。不要将「调用过一次」等同「恢复完成」。

### Q06 · P1 · 配方快照尚不能保证完整冻结和历史重演

**部分探针确认、部分静态确认。** 已修复生效标量限值冻结和 None 状态，也能拒绝坏 JSON。但 [解析](D:/code/DataTrace/src/DataTrace.Collector/SessionRecipe.cs:202) 缺结构/版本校验：`limits:null` 抛 ArgumentNullException；重复 tagId 抛 ArgumentException；`kind:Unknown, limits:[]` 返回 Frozen，空表随后回落当前定义。这里只捕获 JsonException。

v1 兼容分支不使用其冻结覆盖行，而重新查当前 Recipe。新增/删除点位、必填标志、读地址/数据类型仍来自当前配置；[曲线判据](D:/code/DataTrace/src/DataTrace.Collector/CollectEvaluator.cs:184) 和 [基线](D:/code/DataTrace/src/DataTrace.Collector/CollectEvaluator.cs:255) 也使用现状。快照主要存在 ActiveSessions，末站移除后历史记录没有完整判定快照。另 [RecordCode](D:/code/DataTrace/src/DataTrace.Collector/SessionRecipe.cs:84) 对冻结空编码回落当前 active，中途选择型号会让「按冻结默认值判定」与「记录型号」不一致。

修复：带显式 schemaVersion、配置/算法/基线 revision；校验 kind、限值表完整性、重复/null/非法值；旧版本走受控迁移或人工核对；完整生效依据持久化并由历史记录引用。验收覆盖坏结构、无型号后换型、默认值变化、v1 升级、曲线版本变化、末站后历史重演。

### Q07 · P1 · 在线备份验证了文件完整性，没有保证跨库与外置文件的一致时点

**静态确认。** [备份循环](D:/code/DataTrace/src/DataTrace.Infrastructure/Backup/DatabaseBackupService.cs:158) 先发现数据库、逐个在线备份，再复制数据目录；备份 gate 只互斥备份自身，不协调采集、spool、保留或索引更新。备份 config.db 后的采集可出现在月库副本，却没有对应在制索引；spool 已备份事件随后入库也可能被两边同时保存。CRC/SHA256/SQLite quick_check 均不验证这类业务关系。

修复：短暂停采并排空事务做一致快照，或引入可证明的共同检查点和恢复对账；协调保留删除。验收在备份每个边界并发采集/补传/清理，再恢复到新目录，验证记录、索引、pending、MES、曲线和原始归档引用一致。

### Q08 · P1 · 已建立的 Blazor 长连接缺账号撤销和最新权限复核

**静态确认，未做双浏览器撤权实测。** [Web 服务注册](D:/code/DataTrace/src/DataTrace.Web/Program.cs:137) 没有自定义周期重新验证 authentication state 的 provider。路由角色和 UI 检查使用电路已有 principal；[配置变更服务](D:/code/DataTrace/src/DataTrace.Application/Configuration/ConfigurationChangeService.cs:16) 只接审计用户名，没有服务端最新权限校验。HTTP Cookie 验证不能保证已连接电路的每个动作同步撤权。

修复：重新验证电路身份/账号状态，敏感操作按最新权限和 security stamp 校验，撤权中断或拒绝现有电路。验收用户页面已打开时降级、删号、重置密码，旧电路不能继续保存配置/操作设备，且日志记录拒绝原因。

### Q09 · P1 · 升级备份和数据回滚仍固定安装目录 data

**静态确认。** [升级备份](D:/code/DataTrace/deploy/_common.ps1:334) 和 [回滚](D:/code/DataTrace/deploy/rollback.ps1:137) 都拼 InstallDir/data，而启动脚本允许 customer.DataRoot 指向外置目录。启用外置数据时，备份/恢复可能成功退出但没覆盖应用实际读写的数据。

修复：统一解析绝对数据根，清单写入来源与恢复目标并校验；覆盖绝对路径、相对路径、服务启动和失败回退的隔离演练。deploy/README.md 的生产演示账号表也与当前随机引导管理员逻辑冲突，需同步运维文档。

### Q10 · P2 · 故障显示与 healthz 的业务语义不足

[fault 判据](D:/code/DataTrace/src/DataTrace.Collector/StationCollectPipeline.cs:112) 漏 DatabaseWriteFailed；spool 成功、回写成功时工站可变 Idle。快照损坏又被包装成 PlcDriverException，显示成 PLC 通讯失败并写回该码。[healthz](D:/code/DataTrace/src/DataTrace.Web/Services/HealthProbe.cs:51) 不检查月库实际可写、任务最大年龄、未决确认和恢复就绪，主循环活着时工站可能已停采。

修复：把 liveness、业务 readiness、数据持久化、设备确认、需人工核对分别表示；提供结构化 fault code、关联 ID、首次/最近时间、影响和恢复步骤。验收月库拒写、单任务挂起、快照损坏、恢复未就绪都给出正确状态和原因。

### Q11 · P2 · 顺序扫描仍会让一台 PLC 故障影响其他 PLC

[扫描](D:/code/DataTrace/src/DataTrace.Collector/CollectionHostedService.cs:414) 逐 PLC await；冷却能减少重复重连代价，但首次超时和冷却后的重试仍拖延后续设备。驱动异步接口内部有同步网络操作；取消令牌不等于硬 IO 时限。pending 跨全部月库查询还增加触发处理开销。

修复：每 PLC 独立调度、有限并发与等待预算，设备采集/试读/心跳定义优先级。验收拔掉一台 PLC 时其他工站 P95/P99 仍符合实际节拍，按设备和队列分别记录延迟。

### Q12 · P2 · 积压和输入没有明确容量预算与背压

[spool ListAsync](D:/code/DataTrace/src/DataTrace.Infrastructure/Storage/FileSpoolStore.cs:54) 先读入全部请求和曲线再重放；[文件源](D:/code/DataTrace/src/DataTrace.Collector/FileSourceReader.cs:47) 整文件读取；[PLC channel](D:/code/DataTrace/src/DataTrace.Plc/Queue/PlcRequestQueue.cs:43) 无界。资源不足时最需要恢复，整队反序列化反而可能加重内存压力。深分页跨库合并也缺生产规模证据。

修复：spool 分页/逐条读、输入及解压上限、有界队列、磁盘配额和背压，超限可诊断且可恢复。验收以真实峰值速率的一天积压和三年库量测试内存、补传吞吐、查询延迟。

### Q13 · P2 · 保留删除只看会话起始月份，未验证较新数据与未完成引用

[保留任务](D:/code/DataTrace/src/DataTrace.Collector/RetentionHostedService.cs:44) 按月库名删除；后站数据落在会话开始月，因此旧月可含未过期的新记录。未决确认、在制索引、MES 意图、spool 和跨月曲线引用也未在删除前统一检查。

修复：定义对象生命周期，删除前核验实际记录时间和未完成引用；按关联文件完整清理，留下可恢复审计。验收旧会话含新记录、跨月曲线、未投递意图、待确认时不误删。

### Q14 · P2 · 版本号和摘要审计不足以还原完整配置，提交前已有内存副作用

[ConfigVersion](D:/code/DataTrace/src/DataTrace.Domain/Entities/ConfigVersion.cs:3) 是递增计数，没有不可变完整修订。[配置事务](D:/code/DataTrace/src/DataTrace.Application/Configuration/ConfigurationChangeService.cs:86) 保护配置+审计数据库提交，但 [SetActiveRecipeAsync](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/ConfigRepository.Recipes.cs:275) 在外层审计提交前推 hub；审计失败回滚数据库不会撤回已发布状态。摘要也不能恢复全部字段。

修复：提交后发布事件，完整结构化修订和差异、操作者/原因/校验结果，回滚生成新修订；记录引用配置和算法修订。验收模拟审计失败时 DB/hub/cache 一致，历史修订可重演和比较。

### Q15 · P2 · 交付回归门禁未通过，解决方案改名没有更新视觉测试

**E2E 实跑确认。** [VisualGolden](D:/code/DataTrace/tests/DataTrace.E2E.Tests/VisualGolden.cs:54) 仅认 DataTrace.slnx，当前已改 .sln，4 个视觉基线用例全部报根目录无法定位。另两项是 `SettingsWarnsAboutUnsavedEditsAndClearsItAfterSaving` 和 `HoveringAChartShowsThePointReadout` 超时；尚不能仅凭超时确定是产品缺陷还是测试辅助方法/时序问题。

修复：更新根目录定位，诊断两项超时；关键故障注入必须加入 CI。视觉 golden=auto 在环境不匹配时可退化为截图，固定 runner 应另设真正 compare 门禁。原始构建包检查应在固定干净环境通过，不把临时跳过当交付验收。

## 4. 对上一轮修复的核对

| 旧问题 | 本轮结论 |
|---|---|
| F01 单例捕获 scoped | 已改 scoped，容器门禁通过，真实 E2E 宿主能启动 |
| F02 索引载荷 sessionId=0 / 坏载荷作废 | 正确 ID 在事务内填入；坏 JSON 保持 Deferred，新增真实 SaveAsync 回归通过；启动全量恢复仍受 Q05 约束 |
| F03 跨月 pending ID 冲突 | 接口与调用改为 MonthKey+Id，跨月删除/失败计数回归通过 |
| F04 提交后才留证 | 正常月库路径已同事务登记，原窗口有实质修复；末站、设备身份和 spool 故障链未关闭（Q01–Q03） |
| F05 仅冻结覆盖 / 坏 JSON 回落 | v2 冻结生效标量、None、坏 JSON 拒绝已有回归；完整结构、历史和曲线冻结未关闭（Q06） |
| F06 EF migration 缺失 | 迁移/snapshot 补齐，两个上下文无 pending model changes；运行库幂等升级测试通过 |
| R05 同秒孤儿流水冲突 | 毫秒+工站+随机段消除旧确定性冲突，当前未再确认旧缺陷 |
| R09 短帧补零 | 驱动解码前长度校验和 Modbus 截断回归通过；其他真协议仍需故障注入 |
| R07 停机先释放队列 | 顺序已改、队列残留请求取消有进步；新任务生命周期及总体时限未验收（Q04） |
| 其他旧工程缺口 | 备份/撤权/外置回滚/保留/状态/故障隔离/版本/容量仍按 Q07–Q14 跟踪 |

## 5. 五项易忽略能力与六步故障闭环

| 能力 | 当前表现 | 下一步验收 |
|---|---|---|
| 异常处理 | 回写有限次、设备冷却、spool、后台 catch | 故障状态不绕过持久阻塞；事件身份可靠；全局资源和等待有界 |
| 可诊断 | 日志/流水号/诊断/报警有基础 | 明确数据故障与通讯故障，关联 pending/spool/恢复结果 |
| 状态一致与恢复 | 意图/迁移/补传已有实质改善 | 每件数据与设备确认独立持久化，未知实态不可自动重采 |
| 模块化 | 分层和接口较好 | 状态机从 UI/采集编排中独立，动作服务执行最新权限，事务后事件发布 |
| 版本变更 | 程序版本、配置计数、部分审计 | 完整修订、schema 兼容、算法/曲线依据、差异和可验证回滚 |

| 六步 | 当前断点 | 应保存的事实 |
|---|---|---|
| 发现异常 | readiness 不完整 | 模块/工站、故障代码、发生和发现时间 |
| 定位原因 | 数据故障被标通讯失败 | 原因链、事件 ID、版本、日志关联 |
| 安全处理 | 删除留证后仍可采集、旧码可确认新件 | 阻塞原因、设备身份、允许动作 |
| 恢复运行 | 一批对账/异常后继续，缺人工核对协议 | 核对结果、操作者、恢复步骤 |
| 验证结果 | 寄存器读回不等于事件身份一致 | 记录/索引/设备确认一致性结果 |
| 保留记录 | 完整快照和恢复审计不足 | 不可变修订、前后状态、恢复结果 |

## 6. 成熟度与量化验收建议

**实现具备 L2 基础，若干测试/迁移/分层能力接近 L4；整体 L3 尚不通过。** 未做长稳及真机故障验证，不能直接承诺整体已稳定达到 L2。以下为建议验收门槛，需要结合产线节拍签署，不是本轮测得数据。

| 质量维度 | 指标与验收方法 |
|---|---|
| 正确性/数据可靠性 | 每故障场景事件记录数、流水/托盘关联、MES 幂等；受控故障集重复/丢失/误确认=0；离线真实协议联机补验 |
| 稳定性 | 先做 72 小时峰值模拟，再做现场试运行；异常退出=0，句柄/线程/内存无持续增长；报告趋势而不是只报峰值 |
| 性能 | 按 PLC/工站测触发→采集→持久化→确认 P50/P95/P99，单故障 PLC 不突破其他工站约定预算；禁止统一硬设 100 ms |
| 可恢复性 | 提交前后、回写前后、销账前后进程硬退出；月库锁/磁盘满/设备离线；量化 RTO 和实际 RPO，恢复后验证实态 |
| 可观测性 | 关键故障检测时延、漏报率、工程师从故障到根因的时间；未知身份与人工阻塞全部可见 |
| 易用性 | 让首次用户完成配置、试读、查询、故障核对；记录步骤数、误操作率、完成时间，至少覆盖操作员和现场工程师 |
| 可测试性 | 原始构建、1391 核心、346 组件、118 E2E 全绿，关键故障集可重复；固定环境真正视觉比较；新增覆盖按行为而非代码行计 |
| 可维护/扩展 | 替换驱动/新增判定功能的修改范围、回归影响、定位耗时；验证无需改核心状态调度 |
| 安全性 | 旧电路撤权/删号/改密后敏感动作被拒；权限、失败动作和恢复全留痕；部署验证 TLS 或已确认的工控隔离方案 |
| 部署升级 | 新库/旧 EF 库/无 EF 基线库；本地/外置 DataRoot；安装、升级、回滚、全量恢复分别计成功率和耗时，在隔离目录演练 |

建议推进顺序：

1. 关闭 Q01–Q05：事件状态机、设备身份、spool 一致留证、持久阻塞、任务生命周期、恢复就绪。
2. 关闭 Q06–Q09：完整判定修订、一致备份、撤权、外置回滚；补相应故障回归。
3. 修 Q10–Q15：诊断语义、故障隔离、容量预算、保留引用、修订治理、完整交付门禁。
4. 完成长稳、规模、真机与恢复演练后，才按 L3 作现场长期试用决定；涉及人员/设备安全控制另做独立设计验证。

## 7. 最终构建复核

在 E2E 退出、停止代码中间态修正后，再执行 `dotnet build DataTrace.sln -c Release -p:SuppressTfmSupportBuildWarnings=true --nologo`，成功，0 警告、0 错误；随后核心测试 `--no-build` 再次 1391/1391 通过。见 [最终构建日志](D:/code/DataTrace/artifacts/quality-current-final-build.log)、[最终核心 TRX](D:/code/DataTrace/artifacts/quality-current/core-final.trx)。这仍是诊断参数构建，原始门禁失败未关闭。

针对新产物再次构建并运行隔离探针，Q01/Q02/Q06 的输出完全相同；Config/Runtime 模型仍均无 pending changes。见 [最终探针日志](D:/code/DataTrace/artifacts/quality-current-probe-final.log)。本轮不再将旧报告 F01/F03/F06 和 sessionId=0 作为当前缺陷。

审查中的一次诊断构建与 E2E 同时使用常规 bin 输出，发生 DLL 复制占用错误；该冲突是本轮验证安排造成，已在 E2E 退出后消除，不计入产品缺陷。中间编译错误也已在最终构建消失，不列为当前问题。

组件与 E2E 数量沿用本轮先前实际运行结果；停止服务实现变化后未再全量重复浏览器测试。6 项 E2E 失败没有被宣称关闭，视觉根目录失败仍由当前源码确认。后续任何修复都需重跑相关故障回归和完整交付门禁。
