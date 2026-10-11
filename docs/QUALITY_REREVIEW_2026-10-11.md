# DataTrace 修复复审（2026-10-11）

**结论：修复方向正确，但本轮不能通过复审。评分暂维持约 70/100，整体仍不能按 L3「现场可靠」验收。** 这不是认证分数，不宜把新增代码量或通过测试数换算成加分。两个旧问题基本关闭，三个部分修复；新增链路存在可复现的启动、恢复和跨月数据错误，抵消了当前改进带来的可靠性收益。

审查对象：`eca62c9` 基础上的当前未提交工作区，包括新文件和 `.slnx` → `.sln` 变更。对照 [首轮报告](D:/code/DataTrace/docs/QUALITY_REVIEW_2026-10-11.md)。本轮没有修改用户的业务实现或已有测试；只新增本报告及 `artifacts/quality-rereview/` 隔离验证产物。未运行真实设备写入、升级或恢复生产数据。

## 1. 实际验证结果

| 验证 | 结果 |
|---|---|
| 原始 Release 构建 | .NET SDK 8.0.425；54 警告、1 错误。仍被 `System.Collections.Immutable` 的 TFM 检查阻断，切换 SDK/解决方案没有在本机解决问题 |
| 核心测试 | 1381/1381 通过；为进入测试，临时命令参数 `-p:SuppressTfmSupportBuildWarnings=true`，未改仓库门禁 |
| Web 组件测试 | 346/346 通过；同样使用临时命令参数 |
| 真实 Development 启动 | 隔离 DataRoot、仅 localhost 端口，应用在 `builder.Build()` 抛 AggregateException，未启动成功 |
| 真实生产写入逻辑探针 | 首站记录的会话 ID 为 1，事务中保存的索引意图载荷 sessionId 为 0 |
| 索引重放探针 | applied=0、索引仍缺失，但意图 AppliedAt 已写入，失去再次补偿机会 |
| 跨月待确认删除探针 | 两个月记录均为 ID=1；删除新月事件后，旧月事件被删，新月仍保留 |
| 配方损坏与默认值探针 | 损坏快照回落当前上限 999；冻结型号的未覆盖字段仍读取当前点位默认上限 999 |
| EF runtime 模型检查 | `HasPendingModelChanges()` 返回 true；新增运行库表未提供对应 EF migration/model snapshot |
| 全量 E2E | 本轮未重跑；真实 Development 启动已经阻塞，上一轮 E2E 失败不能视作关闭 |

证据目录：[验证产物](D:/code/DataTrace/artifacts/quality-rereview/)。可复跑的独立探针：[Program.cs](D:/code/DataTrace/artifacts/quality-rereview/probe/Program.cs)、[Probe.csproj](D:/code/DataTrace/artifacts/quality-rereview/probe/Probe.csproj)。命令为 `dotnet run --project artifacts/quality-rereview/probe/Probe.csproj -c Release -p:SuppressTfmSupportBuildWarnings=true`，每次使用独立临时月库。

诊断参数只绕过包检查；不表示原始交付构建门禁已通过，也不证明所有包兼容性已验证。

## 2. 新增实现中的阻塞问题

### F01 · P1 · 单例索引对账服务捕获 Scoped 在制仓储，应用启动失败

[注册处](D:/code/DataTrace/src/DataTrace.Infrastructure/InfrastructureServiceCollectionExtensions.cs:100) 将 ISessionIndexRelay 注册为 Singleton，但 [SessionIndexRelay 构造函数](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/SessionIndexRelay.cs:21) 直接注入 Scoped 的 IActiveSessionStore，后者又持有 Scoped ConfigDbContext。

**已实跑复现：** Development 的容器校验报 `Cannot consume scoped service 'IActiveSessionStore' from singleton 'ISessionIndexRelay'`，在 Program.cs 的 builder.Build 阶段终止。生产关闭 scope 校验时不能当作解决：这会使对账器长期持有根作用域 DbContext，其跟踪状态和线程安全仍存在问题。

修复：对账器注册 Scoped，并由已有 SessionIndexReconciler 每轮创建的 scope 解析；或单例内部以工厂创建独立作用域/context。增加真实宿主/ValidateScopes=true 的启动测试，不能仅验证替身容器能创建采集流水线。

### F02 · P1 · 索引意图保存了会话 ID=0，重放会把有效意图作废

[事务内序列化](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/RuntimeStore.cs:103) 发生在首次 SaveChanges 后，但仅 Record.PalletSessionId 已获生成 ID；request.ActiveSession.SessionId 仍为初始 0。该字段直到 [提交后的索引更新](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/RuntimeStore.cs:280) 才被赋值。

[重放](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/SessionIndexRelay.cs:99) 用载荷的 sessionId 查询月库，会查不到 ID=0，返回 false；外层仍 [设置 AppliedAt](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/SessionIndexRelay.cs:53)。因此本该补回的首站索引永久缺失。

**探针原始结果：** `committed record session=1; persisted payload session=0`、`Relay applied=0; restored index=MISSING`、`Lost index intent marked applied: True`。探针调用真实 RuntimeStore.SaveAsync，在提交后注入索引更新异常，验证的载荷就是实现实际产生的载荷，不是人工拼的正确样本。该状态也对应两次提交间硬退出留下的意图。

修复：事务内序列化前填入实际会话 ID，或让意图拥有独立且完整的会话定位键；载荷非法/损坏应留待诊断而不是归类成正常过期。启动采集前还要有对账完成屏障，当前后台对账并未阻止采集先运行。

现有 `Session_index_intent_replays_after_hard_exit` 测试手工填入正确 SessionId，绕过了生成载荷的失败点；它也没有真正终止/重启进程。应补「真实 SaveAsync 生成意图 → 提交后索引失败/硬退出 → 新作用域重放」测试。

### F03 · P1 · 待确认记录的 ID 仅月库内唯一，跨月更新/删除会命中另一件

[DeleteAsync](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/PendingWriteBackStore.cs:65) 与 [RecordFailedAttemptAsync](D:/code/DataTrace/src/DataTrace.Infrastructure/Persistence/PendingWriteBackStore.cs:108) 接口只有 long id，按月份逐库找第一条 Id 匹配就执行。每个月库的自增 ID 会重用。[确认成功调用](D:/code/DataTrace/src/DataTrace.Collector/StationCollectPipeline.cs:217) 丢掉了已知的 pending.MonthKey。

**探针原始结果：** 202609 的 OLD 与 202610 的 NEW 均为 ID=1，调用 DeleteAsync(NEW.Id) 后 `OLD still exists=False; NEW still exists=True`。旧件的防重复保护被删除，新件已确认但留证仍在；退避更新也会写错事件。

修复：身份改成 `(MonthKey, Id)` 或真正全局事件 ID，只打开指定月库；同时验证工站与事件身份。测试至少包含两个不同月份、相同 ID、不同工站/流水号，覆盖删除和失败计数更新。

### F04 · P1 · 待确认留证仍在回写失败后保存，断电窗口没有消除

[Pipeline finally](D:/code/DataTrace/src/DataTrace.Collector/StationCollectPipeline.cs:105) 先做 PLC 回写，直到回写失败并发布事件后才 [保存 PendingWriteBack](D:/code/DataTrace/src/DataTrace.Collector/StationCollectPipeline.cs:155)。采集月库事务并没有写入待确认意图。

记录提交后、finally 留证前硬退出时，库有记录、PLC 仍是触发值、PendingWriteBack 不存在，重启仍会重新采集。落库失败转 spool 时也没有与 spool 一起可靠保存确认状态；新增 finally 的月库写入还可能因同一数据库故障再次失败。现有幂等测试从「正常采集成功，再人工加入留证」开始，只证明已有留证时能重试，并未证明留证不会丢。

另一边界是 [确认重试](D:/code/DataTrace/src/DataTrace.Collector/StationCollectPipeline.cs:209) 没有读取当前托盘或 PLC 事件号来核对身份：若设备已超时换件、旧 pending 仍在，新的触发可能被旧结果码确认。仅凭触发寄存器等于 1 不能证明仍是原事件。

修复：将确认意图与采集记录同事务持久化（spool 请求也包含这份意图），提交后只进行设备确认；确认前核对设备事件身份与原地址/协议版本。无事件身份时进入人工核对/阻塞状态，不能把旧结果应用于不明新件。硬退出测试应覆盖提交后、留证前、回写后、删除留证前等边界。

### F05 · P1 · 配方快照损坏时仍静默使用当前配方，冻结范围也不完整

[FromSnapshot](D:/code/DataTrace/src/DataTrace.Collector/SessionRecipe.cs:98) 对坏 JSON 返回 null，[Select](D:/code/DataTrace/src/DataTrace.Collector/SessionRecipe.cs:25) 随即按旧编码查当前 Recipe。调用方没有区分旧会话确实无快照与新快照损坏，因此注释中的「绝不静默换规则」没有兑现。反序列化 DTO 的 null Limits 等结构问题也没有完整校验。

快照只存 Recipe.Limits 的覆盖值，null 字段仍依赖当前 TagDefinition；未选型号时 Snapshot 返回 null，Select 也会使用后续当前型号。点位默认值改变或中途新选型号，旧件规则仍可能变化。曲线 criteria/基线没有冻结；快照保存在 ActiveSessions，末站移除索引后也没有完整持久化修订链可供历史重演。

**探针结果：** 坏 JSON 快照解析为空后选择了当前上限 999；冻结 Recipe 没有覆盖值时，Resolver 仍返回当前点位上限 999。

修复：明确「无型号」也是冻结状态，保存全部实际生效限值/判定版本；损坏、不可用版本必须阻塞并留诊断。兼容旧会话时需要明确迁移/核对策略，不能用统一 fallback 掩盖损坏。

### F06 · P2 · 新运行库模型只有 SQL 补表，缺少对应 EF migration

新增 SessionIndexIntents/PendingWriteBacks 已加入 RuntimeDbContext 与 RuntimeSchema v3，但运行库 EF migration/model snapshot 未更新；探针 `HasPendingModelChanges()` 返回 true。手工建表使当前测试能跑，不代表模型变更已进入统一迁移链。本仓库 README 明确要求模型变更提交 migration，并且该检查是上一轮已有交付验证项。

修复：补运行库 migration 和 snapshot，统一与 RuntimeSchema 旧库补丁的边界，验证全新库、旧 EF 库与无 EF 基线旧库都不会重复建表，Config/Runtime 两个上下文均无 pending model changes。

## 3. 上一轮 14 项问题逐项结论

| 原问题 | 复审状态 | 依据 |
|---|---|---|
| R01 回写失败重复采集 | 部分修复，未关闭 | 已有 pending 时能重试确认；提交到留证之间仍可断电，新机制还存在跨月身份和设备事件核对问题（F03/F04） |
| R02 月库与在制索引窗口 | 部分修复，未关闭 | 意图进入同事务是正确方向；DI 启动失败、载荷 ID=0、对账先于采集的屏障缺失（F01/F02） |
| R03 在线备份一致性 | 未修复 | 备份服务和协调机制未见改动 |
| R04 配方冻结 | 部分修复，未关闭 | Recipe 覆盖值快照和读取异常阻塞有进步；损坏/空型号/默认值/曲线版本仍未冻结（F05） |
| R05 孤儿流水同秒冲突 | 基本关闭 | 毫秒、工站与随机段区分，原来的确定性同秒冲突已消除；建议补同秒多托盘回归 |
| R06 长连接撤权 | 未修复 | Web 认证 provider、动作授权和账号生命周期未见修复 |
| R07 停机收尾 | 未修复 | StopAsync 顺序未改，新增确认任务仍 fire-and-forget、未汇合 |
| R08 外置 DataRoot 回滚 | 未修复 | deploy/_common.ps1 与 rollback.ps1 未改 |
| R09 短帧补零 | 基本关闭 | 解码前检查实收长度；Modbus 真协议短帧拒绝和完整全零响应测试通过；其他品牌仍需同类覆盖 |
| R10 保留删除边界 | 未修复 | 保留服务未改，新增未确认/索引意图同样缺少删除前保护 |
| R11 状态/探活语义 | 未修复 | DatabaseWriteFailed 仍不在 fault 判据，healthz 仍是主循环心跳 |
| R12 PLC 故障隔离 | 未修复 | 仍顺序扫描；pending 每次命中还跨所有月库查找，增加热路径 DB 开销 |
| R13 配置追溯与提交前副作用 | 未修复 | 未建立不可变配置修订链，原审计字段与缓存/hub 事务外副作用未改 |
| R14 资源预算 | 未修复 | spool 整队读取、全文件读入、无界队列仍在 |

**计数：2 项基本关闭、3 项部分修复、9 项未修复。** 部分修复不能计作上线风险关闭；新增缺陷需要另外跟踪，不能从旧问题清单中消失。

## 4. 本轮测试为什么仍然全绿

- 新增幂等测试主动构造已存在的正确 pending，没有验证真实回写失败路径如何持久化 pending。
- 新增索引测试人工注入正确 SessionId，没有验证 RuntimeStore 序列化的真实载荷，也没有验证宿主 startup barrier。
- 测试 harness 未开启生产宿主同等的 ValidateScopes 校验，单例捕获 scoped 在制仓储未被发现。
- pending 测试没有跨月同 ID；冻结快照没有覆盖损坏、未选型号、默认点位/曲线变化。
- Web 测试多为组件替身注册，不能代替真实应用启动。修改组件测试等待与断言也不能关闭上一轮未修复的 E2E 辅助方法错误或视觉 capture-only 门禁。

这批测试不是没有价值；它们证明了样本状态下的功能，但应补实际故障生成路径和宿主/跨月边界，才有资格支撑可靠性承诺。

## 5. 建议的下一轮验收顺序

1. 先修 F01：真实 Development/Production 启动、ValidateScopes/ValidateOnBuild 校验均通过，后台服务独立作用域。
2. 修 F02/F03：真实提交产生的索引意图可重放，非法载荷留证；所有 pending 操作按完整事件身份定位。
3. 修 F04：月库同事务确认意图、spool 一致留证、启动恢复屏障、设备身份核对；做真实进程级故障注入。
4. 修 F05/F06：完整冻结语义与损坏阻塞，统一 EF 迁移链，旧库升级回归。
5. 继续关闭未改的 R03/R06/R07/R08 等上线风险，再恢复原始零警告构建、完整 E2E 与真实视觉比较门禁。

评分可在上述复现失败转为回归测试通过后再上调。现阶段不建议仅因 1381 项单测通过就评为 80 分以上，也不建议宣称修复已完成或成熟度已达到 L3。
