# 改进实施与验证（2026-10-12）

本轮由主代理指挥 GPT-6 Luna 分组实施并整合。起始工作区干净；代码、文档和测试直接保留在当前工作区，没有提交或发布到现场。

## 七项建议的落地范围

| 建议 | 本轮的软件改进 | 验收边界 |
|---|---|---|
| 崩溃恢复 | 独立子进程与真实 SQLite/spool，临界点通过 IPC 通知后强杀，再从同一隔离数据根恢复 | 真实断电、介质故障和设备重启仍需现场条件 |
| 稳定设备事件号 | 可选 UInt64 事件身份及 PLC 条件确认请求/持久回执；冻结身份及协议地址，新增配置/运行库迁移 | PLC 程序须实现原子条件处理、幂等性和跨重启保持；详见协议文档 |
| 现场指标 | 有界站级近 15 分钟 P95/P99、单调时钟任务年龄、spool/MES 最老积压、可配置超龄状态 | 最近窗口最多每站 512 件、最多 128 站，不冒充全天或全历史分位数 |
| 历史查询 | 跨月稳定排序游标、页面接入、SQLite BINARY 字符串排序、共享计数缓存及写入失效 | 基准使用合成隔离数据；吞吐不能代表现场 PLC 或生产容量 |
| 视觉门禁 | 固定 Playwright Chromium、提交环境指纹、严格 compare；重录是独立人工审核流程 | 远端 CI 仍须执行；不同字体/系统渲染需在固定 runner 上核对 |
| 恢复发布验收 | 隔离安装、升级、备份校验、数据回滚，接入 CI；备份停启与维护屏障等待分别测量 | 不包含真实客户生产升级或三年最大数据量验收 |
| 页面与文档 | 提取报表趋势、MES 出站状态、工站试读与条件协议组件；历史记录注明时间范围 | 这是按业务区块的渐进拆分，大页面其余职责可继续细化 |

## 配置与使用

新增任务超龄阈值为 `DataTrace:Diagnostics:CollectionMaxAgeSeconds`。默认 `0` 关闭，现场按合法最长采集时间设定后启用，不能从扫描周期猜测。示例设置为 30 秒：

```json
{ "DataTrace": { "Diagnostics": { "CollectionMaxAgeSeconds": 30 } } }
```

PLC 事件协议默认关闭。工站页可填写三个地址，保存前校验完整性和协议块重叠；详见 [设备协议与现场验收](DEVICE_EVENT_PROTOCOL.md)。新字段通过 EF migration 升级，运行结构版本及无迁移历史月库兼容接管同时更新。

视觉基线独立审核流程见 [golden 说明](../tests/DataTrace.E2E.Tests/golden/README.md)。查询规模测试见 `tests/benchmarks/run-query-performance.ps1`；备份/交付演练见 `deploy/acceptance-isolated.ps1`。

本机原先仅有 .NET 10。本轮将 .NET 8.0.425 安装到独立的 `%LOCALAPPDATA%\DataTrace\dotnet8`，保留项目的 .NET 8 SDK/TFM 和警告即错误设置。

## 本机验证证据

所有测试和演练使用独立临时数据目录与模拟设备，没有接入客户 PLC。

- .NET 8 Release 解决方案构建：0 警告、0 错误。
- 核心单元/集成测试：1450/1450 通过（排除独立 Performance 类别）；组件测试：377/377 通过。TRX 位于 `artifacts/improvement-validation/core-final`、`web-final`。
- 四个进程强杀/重启场景均通过：事务提交前、业务与待确认提交后、spool 持久化后、补传提交后未删除 spool。它们验证进程异常退出的持久化行为，不等同于真实介质断电。
- 真实 SQLite 事务回滚重试、spool 原始判定与条件确认一致性、无 EF 历史旧月库升级、超龄报警及匿名探活均纳入回归。
- 查询基准覆盖 36 个月、360,000 条记录，目标页结果一致。冷首页 78 ms；时间排序游标逐页遍历至深页累计 296 ms、一次 OFFSET 72 ms；耗时排序分别为 7325 ms、1487 ms。逐页遍历包含 21 次请求，这些累计数字不证明游标比一次 OFFSET 更快。完整数据与复现步骤见 [查询基准](../tests/benchmarks/query-performance-baseline.md)。
- 在线备份基准实际调用 `DatabaseBackupService`，8 MB/4 文件加一份 SQLite：备份 158.79 ms，独占维护锁 154.52 ms，后续采集读锁等待 140.10 ms。另验证 5 字节/4 文件的分配边界。原始 JSON 位于 `artifacts/improvement-validation/backup-8mb.json` 和 `backup-small.json`；不能外推为三年生产数据的停顿。
- 四张视觉基线已在 Windows Chromium 148.0.7778.96 独立录制、逐张目检，再独立严格比较，4/4 通过；设置页临时目录、健康值与监听端口均精确遮罩。
- 完整 Chromium 浏览器回归最终 118/118 通过，包含严格视觉比较。两项旧看板用例已按现有换行标题、完整曲线展示行为更新，并实际验证标题边界和曲线可见内容；未改产品布局迁就旧断言。TRX 位于 `artifacts/improvement-validation/e2e-final`。
- 隔离部署验收通过安装、升级、全量备份校验和数据回滚，回滚后 8 MiB marker 的 SHA-256 与原值相同，升级后新增文件已消失；应用重启后再次校验通过。离线停机、备份和重启共 10.717 秒。父进程等待验收子进程退出后同步删除唯一临时目录，已确认清理完成。

成功部署演练的输出留在本轮工具会话记录中，没有独立日志文件。三个较早失败演练的临时目录已核对属于本轮，相关实例已停止，但自动审批拒绝删除操作（`blocked by policy`，未提供具体原因），因此仍保留于 `%LOCALAPPDATA%\Temp`：

- `datatrace-acceptance-5086cb25466c4cc5bb9ea9012cac0b95`
- `datatrace-acceptance-5cd1f18a21f741d58281d599321228df`
- `datatrace-acceptance-f1fb2c66a0e640bd82f0f49c024c1783`

## 仍需现场条件的工作

- 真实 PLC 程序联调、断线/迟到应答、PLC 重启及断电。
- 工控机真实断电、介质损坏与异盘备份恢复。
- 按客户节拍、曲线量和设备数量的 72 小时长稳及容量验收。
- 在远端固定 runner 执行新增 CI 门禁；本机执行不等于远端 CI 已通过。
