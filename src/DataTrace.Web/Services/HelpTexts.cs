using DataTrace.Domain.Constants;
using DataTrace.Domain.Validation;

namespace DataTrace.Web.Services;

/// <summary>
/// 一条概念详解。三段固定结构：是什么 / 怎么算或边界 / 影响什么。
/// </summary>
/// <param name="Title">概念名，也是 ⓘ 按钮可访问名里的那一段（必须短，不能含页面上按钮的文字，见 HelpTextsTests）。</param>
/// <param name="How">口径与边界：怎么算、取值范围、判定规则。写不下就说明该去查哪儿。</param>
/// <param name="Impact">对现场的影响：会不会判废、会不会丢数据、多久生效。</param>
public sealed record HelpTopic(string Title, string How, string Impact);

/// <summary>仅供「本页说明」使用的页面级操作指南；与短小的概念 tooltip 分开维护。</summary>
public sealed record PageHelpSection(string Title, IReadOnlyList<string> Paragraphs);

/// <summary>
/// 概念详解的<b>唯一</b>出处。界面上的 ⓘ（<c>InfoHint</c>）与包住芯片的 tooltip（<c>HelpContent</c>）
/// 都从这里取字，同一概念在多页只写一份 —— 以前"直通率""限值三档"在各页各写一版，改一处必漏一处。
/// </summary>
/// <remarks>
/// 具名字段而不是字典：拼错立刻编译报错，也不用维护一套字符串键。
/// 文案长度与禁用词由 HelpTextsTests 兜底（tooltip 是补充说明，不是小作文）。
/// <b>凡是复述系统行为的数字，一律引用 <see cref="SystemDefaults"/></b> ——
/// 写死在字符串里的话，改了常量 tooltip 就会对现场说谎，而单测抓不到。
/// </remarks>
public static class HelpTexts
{
    // ---------- 看板与全站共用 ----------

    public static readonly HelpTopic YieldRate = new(
        "直通率",
        "= OK ÷ (OK + NG)，「未判定」既不进分子也不进分母。",
        "分母为 0 时显示 —；它是质量指标，不阻断生产。");

    public static readonly HelpTopic TodayScope = new(
        "今日统计",
        "按北京时间自然日统计，00:00 起算，跨零点自动归零。",
        "数据查询的「今天」按 Web 主机本地日期预设；主机不在北京时间时，两页统计日期可能不同。");

    public static readonly HelpTopic JudgementThreeState = new(
        "判定三态",
        "OK = 全部点位都在规格限内；NG = 任一点位超规格限或该取的值取空；未判定 = 还没得出结论。",
        "未判定多出现在采集中断或读取失败时，排查先看结果码。");

    public static readonly HelpTopic LimitThreeTiers = new(
        "限值三档",
        "合格 = 规格带（红线）内；预警 = 落在黄线与红线之间；超限 = 越出红线。",
        "只有超限判废；预警单独进「预警 Top N」，黄线配到红线外时按红线收敛。");

    public static readonly HelpTopic StaleData = new(
        "采集状态",
        "PLC x/y = 已连接 ÷ 已配置台数；采集中 = 正在取数的工站数。",
        $"心跳超过 {SystemDefaults.StaleHeartbeatSeconds} 秒没刷新才提示「数据已停止更新」；关掉采集会改用专用告警。");

    public static readonly HelpTopic Cadence = new(
        "最近节拍",
        "距最近一次工站完成的时间；产线本就没料时变慢不算异常。",
        $"超过 {SystemDefaults.CadenceWarnSeconds} 秒转黄、{SystemDefaults.CadenceIdleSeconds / 60} 分钟转灰，要和顶部「数据已停止更新」的告警一起看。");

    // ---------- 数据查询 ----------

    public static readonly HelpTopic RangeScope = new(
        "区间与命中数",
        "命中数是分页前的全部条数，不是当前页条数；区间含起止两天的整日。",
        "导出会不会被截断也按这个数字判断，翻页不会改变它。");

    public static readonly HelpTopic MatchMode = new(
        "匹配方式",
        "托盘码与流水号都是「包含」匹配而非前缀匹配：填 P1 会命中 P1001。",
        "字符太少会命中大量记录、查询变慢；比较区分大小写。");

    public static readonly HelpTopic ExportLimit = new(
        "导出上限",
        $"一次最多导出前 {SystemDefaults.ExportRowLimit:N0} 条（按当前筛选与排序取）。",
        $"命中更多时会提示「共命中 N 条，已导出前 {SystemDefaults.ExportRowLimit:N0} 条」，请缩小区间分次导。");

    public static readonly HelpTopic ResultCode = new(
        "结果码",
        $"采集握手码：1 = 触发待采集，2 = 采集成功，3~{ResultCodes.ArchiveFailed} 是各类异常（读失败、写库失败等）。",
        "它与限值判定独立；3/6/7 查通讯或数据库。PLC 失败记入审计，不计产品记录与产量。");

    public static readonly HelpTopic RecipeScope = new(
        "型号口径",
        "记录按采集当时的型号落库；「未选型号」指那批没绑型号的记录。",
        "全部型号汇总可合并历史编码；筛选单个编码时仍按该编码精确匹配，不会自动包含旧编码。");

    // ---------- 报表 ----------

    public static readonly HelpTopic AverageYield = new(
        "平均直通率",
        "= 区间 ΣOK ÷ Σ(OK + NG)，按件加权，不是每日直通率的算术平均。",
        "各日产量悬殊时两个算法会差得明显；口径与看板「直通率」一致。");

    public static readonly HelpTopic IssueShare = new(
        "Top N 占比",
        "只统计超规格（判废）的次数，预警不计入不良；榜单最多列 10 项。",
        "榜内占比之和通常不到 100%，差额就是没进榜的点位，必要时按点位逐项查。");

    public static readonly HelpTopic TrendSampleLimit = new(
        "采样上限",
        $"趋势图与过程能力单次最多统计最近 {SystemDefaults.TrendSampleLimit:N0} 个采样点。",
        "截断时页面会明确提示；要更早的数据请缩小区间。");

    public static readonly HelpTopic Capability = new(
        "过程能力",
        "Cpk 用组内波动（相邻点移动极差），Ppk 用总体波动（含批间偏移）。",
        "Cpk ≥ 1.33 达标、1.00~1.33 勉强、低于 1.00 不足；样本不足时不下结论。");

    public static readonly HelpTopic SegmentAsterisk = new(
        "分段与估计",
        "分段后每段独立算控制限与移动极差，段与段之间不能直接比 Cpk。",
        "带 * 的段没落库规格限、按当前配置估计；换刀调机与改限值同时发生时段数会变多。");

    // ---------- 用户 ----------

    public static readonly HelpTopic Lockout = new(
        "账号锁定",
        $"连续 {SystemDefaults.LockoutMaxFailedAttempts} 次登录失败自动锁定 {SystemDefaults.LockoutMinutes} 分钟，锁定期间口令正确也登录不了。",
        "到点自动解除；管理员可点「解除锁定」立即清零失败次数。");

    public static readonly HelpTopic RoleScope = new(
        "角色权限",
        "调岗改角色即可，不必删号重建；一个账号可以同时有多个角色。",
        "角色写在登录凭据里，对方要重新登录才受限；撤管理员角色时注意别撤掉最后一个。");

    public static readonly HelpTopic DeleteUser = new(
        "删除用户",
        "删除后该账号不能登录、操作不可恢复；系统至少保留一个管理员。",
        "删除当前登录账号被禁止；每次删除都写审计，事后可查操作人与被删账号。");

    public static readonly HelpTopic UserNameImmutable = new(
        "用户名",
        "用户名是登录名，创建后不可修改，写错了只能删号重建。",
        "它就是审计日志里的「操作人」；3~20 位，只允许字母、数字与 - . _ @ +。");

    // ---------- 系统设置 ----------

    public static readonly HelpTopic SaveToDispatch = new(
        "配置下发",
        "带着改动离开本页时会先拦一次确认，避免改完没保存就走了。",
        "本页只管系统级参数；PLC 与工站的参数在各自页面保存，不受这里影响。");

    public static readonly HelpTopic ScanInterval = new(
        "扫描间隔",
        "采集端轮询 PLC 的周期。调小只是更频繁地问，不代表数据会更准。",
        $"范围 {SettingsLimits.MinScanIntervalMs}–{SettingsLimits.MaxScanIntervalMs} 毫秒；太小常表现为通讯超时或丢点。");

    public static readonly HelpTopic WriteRetry = new(
        "写回重试",
        "「次数」是总尝试次数：填 0 或 1 都只写一次；「间隔」是两次尝试之间的等待。",
        $"次数 {SettingsLimits.MinWriteRetryCount}–{SettingsLimits.MaxWriteRetryCount}，间隔 {SettingsLimits.MinWriteRetryDelayMs}–{SettingsLimits.MaxWriteRetryDelayMs} 毫秒；间隔离太久会拖长该工站周期。");

    public static readonly HelpTopic ConfigSource = new(
        "配置来源",
        "带「只读」的徽标写在安装目录的配置文件里（customer.json / appsettings），界面上改不了。",
        "「运行时」是实时算出来的（只反映当下）；「配置库」才是可在这里改、保存后写库并下发的那些。");

    public static readonly HelpTopic Retention = new(
        "保留年数",
        "按整月清理：保留 N 年，就是 N 年前那个月之前的整月记录库与曲线文件被删掉。",
        "删除不可恢复也没有回收站；缩短年限前请先确认那些旧数据不再需要。");

    public static readonly HelpTopic MesOutbox = new(
        "MES 积压",
        "待推送 = 已采集但还没成功发给 MES 的记录条数。",
        $"积压不会丢记录，系统按退避重试；超过 {SystemDefaults.MesBacklogWarnHours} 小时未成功会红字告警，先查地址与网络。");

    // ---------- 工站配置 ----------

    public static readonly HelpTopic TriggerValue = new(
        "触发值",
        $"触发与回写共用同一寄存器：1 = 触发，2–{ResultCodes.ArchiveFailed} 是采集端写回的结果码。",
        "填 0 会在复位或上电时误触发；填回写码则回写后仍等于触发值，每个扫描周期都会重复采一次。");

    public static readonly HelpTopic TagDataSource = new(
        "点位数据来源",
        "PLC 按地址读寄存器。数据文件：JSON 用 a.b.c，CSV 用表头列名。",
        "文件源每次触发都重读文件：读不到或归档失败就回写失败码且不落库；曲线仍按 PLC 地址读。");

    public static readonly HelpTopic BoolAddress = new(
        "布尔点位",
        "Bool 点位要填字地址（如 D100、MW10），判定规则是该字非 0 即为真。",
        "填位地址（M100、DB1.DBX0.0）不报错，但读取计划只收字地址、会被静默丢掉，表现为值恒为 0。");

    public static readonly HelpTopic FirstLastStation = new(
        "首末站",
        "首站负责建立托盘会话，末站负责关闭会话并把结果上报 MES。",
        "缺首站时后续记录被当成跳站异常；缺末站则会话只增不减、MES 永远收不到上报。");

    // ---------- PLC 连接 ----------

    public static readonly HelpTopic PlcEnabled = new(
        "连接启停",
        "停用后这台连接下的所有工站立即停止采集。",
        "启停状态算进连接签名，改动只会重建这一台 PLC 的连接，其它连接不受影响。");

    public static readonly HelpTopic Heartbeat = new(
        "心跳",
        "按周期往心跳地址写递增（或 0/1 翻转）值，用于向 PLC 证明上位机仍在工作。",
        "只收字地址：填成位地址会被跳过并告警，采集照常；写周期最小按 200 毫秒兜底。");

    public static readonly HelpTopic MergeGap = new(
        "合并间隙",
        "地址间隔小于它的读请求会合并成一次批量读，默认 16 个字。",
        "合并越多、请求越少但单次读越长；它算进连接签名，改了会重建这台连接的队列。");

    public static readonly HelpTopic SimulatorBrand = new(
        "模拟器品牌",
        "模拟器不走网络，读写落在本机内存，且与「PLC 仿真」页共用同一份寄存器。",
        "仿真写进去的值会被采集读走并落库：用它验证流程没问题，但它不是只看不写。");

    // ---------- 产品型号 ----------

    public static readonly HelpTopic RecipeCode = new(
        "系统编码",
        "由系统自动分配的内部标识，用于采集记录追溯、查询筛选和曲线基线分桶。",
        "编码固定；改名称不影响历史。复制型号使用新编码，不继承历史样本或基线。");

    public static readonly HelpTopic RecipeEnabled = new(
        "型号启用",
        "型号未选择或已被停用时，采集一律回落到点位自带的默认限值。",
        "停用的型号不能再设为当前；停用当前型号之前会先确认。");

    public static readonly HelpTopic RecipeCopy = new(
        "复制型号",
        "复制限值字段，并由系统分配新的内部编码；不会继承源型号的波形基线或历史样本。",
        "复制后的型号不会自动设为当前。新编码的基线需从新型号的采集样本重新积累。");

    // ---------- 波形基线 ----------

    public static readonly HelpTopic BaselineSampleCounts = new(
        "样本口径",
        "「取到」是所选序列在区间内的总条数，包含其他型号、NG 和未判定；「型号不匹配」单列其他型号样本。",
        "最近 30 条独立评估，不参与建模；更早的 OK 样本建基线。影子四格只统计 OK/NG。");

    public static readonly HelpTopic RecipeMismatch = new(
        "型号不匹配",
        "区间内按采集当时的型号统计、不属于本型号的样本数。",
        "改码后旧编码仍算本型号；未选型号时只有型号为空的那些记录才算本型号。");

    public static readonly HelpTopic DeviationThresholds = new(
        "偏离门槛",
        "综合偏离是各维度 z 值的均方根（明细与芯片上显示的就是它），明细只列偏离 ≥ 2σ 的维度。",
        "综合 ≥ 3σ 或单维 ≥ 4σ 判异常，综合 ≥ 2σ 判可疑；零波动维度被打破也会计入。");

    public static readonly HelpTopic OnlineBaseline = new(
        "在线基线",
        "后台每 5 分钟用最近 30 天内的 OK 样本重建一次，样本不足 20 条的序列不入缓存。",
        "本页是按所选区间现算的，两者互不影响；型号切换期间会显示旧缓存型号，后台刷新完成后新记录才会使用新基线。");

    // ---------- 审计日志 ----------

    public static readonly HelpTopic LogTimeRange = new(
        "时间区间",
        "按自然日算，与数据查询页同一套口径：选 9-20 ~ 9-26 就是这七天的 00:00 到 23:59。",
        "留空表示不限时间。要圈定某一天，起止都选那一天即可。");

    public static readonly HelpTopic LogTimestamp = new(
        "时间列",
        "写入时取服务器本地时钟，未做时区转换。",
        "跨时区排查时要先确认服务器时区；点表头可切换最新在前 / 最早在前。");

    public static readonly HelpTopic LogEntityKey = new(
        "键列",
        "「键」指向这条日志针对谁，由写入方决定：工站写编码，点位写「工站码/点位名称」。",
        "同一个人改不同对象时靠这一列区分；读不懂时配合「对象」列一起看。");

    public static readonly HelpTopic LogChange = new(
        "变更列",
        "只有变更类动作才渲染「旧 → 新」；左侧显示「新增」表示这件事本来就没有旧值。",
        "登录、退出、登录失败、解除锁定、导出属于「发生了一件事」，说明直接写在这一列。");

    public static readonly HelpTopic LogKeyword = new(
        "关键字",
        "包含匹配，可搜索用户、动作、对象、键、变更内容、操作结果、来源、来源 IP 与关联 ID。",
        "中文名可匹配对应动作码；粘贴关联 ID 可追查请求。");

    // ---------- PLC 仿真 ----------

    public static readonly HelpTopic SimAutoRun = new(
        "自动跑线",
        "由后台按托盘间隔持续触发仿真工站，模拟真实产线的产出。",
        "写进去的是本机模拟器寄存器，采集会照常落库：Production 下开着时，看板数字不代表真实产线。");

    public static readonly HelpTopic SimPalletInterval = new(
        "托盘间隔",
        "只是走完一个托盘之后的等待时间。",
        "站间还要固定等 350 毫秒、每站最多等上位机回写 8 秒，所以走完整线远不止一个间隔那么快。");

    public static readonly HelpTopic SimNgPercent = new(
        "NG 比例",
        "每个托盘抽一次随机数，小于该比例才注入不良，且每个托盘最多挑一个工站。",
        "只在启用且配了规格限的数值点位上写限外值；该工站没有这类点位时这一轮就出不了 NG。");

    public static readonly HelpTopic SimRunLine = new(
        "走完一条线",
        "按产线顺序逐站触发，只取启用、品牌为模拟器、且所属 PLC 也启用的工站。",
        "任一站 8 秒等不到回写就中断并如实报「未走完」；采集未启用时会一直卡在等回写。");

    public static readonly HelpTopic SimLastWriteBack = new(
        "最近回写",
        $"显示采集端写回触发寄存器的响应码：2 是采集成功，3–{ResultCodes.ArchiveFailed} 分别是读失败、托盘码非法、校验失败等。",
        "它不是 PLC 写的值；仿真等不到这个回写（寄存器一直等于触发值）就判超时。");

    // ---------- 记录明细 ----------

    public static readonly HelpTopic LimitColumns = new(
        "限值列",
        "四道限值随记录一起落库，界面按记录当时的值显示。",
        "上下限都空的旧记录（早于限值落库那一版）看起来像没配限值，导出时这类格子也留空。");

    public static readonly HelpTopic OverTolerance = new(
        "超差",
        "必填点位取到空值时直接算超规格，所以数值列显示「-」的行也可能判超差。",
        "这种行要按结果码与错误信息去查取数失败，不是核对差了多少。");

    public static readonly HelpTopic CurvePointCount = new(
        "曲线点数",
        "点数栏是采集时配置、随记录落库的值；图上是抽稀后的样子（最多画 600 点）。",
        "导出的是成对全量数据，按图上数点会误以为丢了点。");

    // ---------- 型号限值与曲线判据的对话框 ----------

    public static readonly HelpTopic LimitMergeRule = new(
        "生效限值",
        "对话框里的生效值是「本型号覆盖值 ⊕ 点位默认值」逐字段合并出来的。",
        "留空表示沿用默认值：只改一侧黄线，就可能撞上你没看到的默认红线而被拦下。");

    public static readonly HelpTopic TargetValue = new(
        "目标值",
        "目标值只作为 SPC 的中心线，不随记录落库。",
        "报表里的目标线取当前配置：改了目标值，历史区间的对照线也会跟着变。");

    public static readonly HelpTopic LimitEffect = new(
        "限值生效",
        "保存后配置版本自增，采集端下一轮取快照时才用新限值。",
        "已采集的记录不会重算；型号未启用或不是当前型号时，覆盖值一律回落默认限值。");

    public static readonly HelpTopic CoverablePoints = new(
        "可覆盖点位",
        "只有数值型点位（整型与浮点）能配覆盖值。",
        "布尔与字符串点位不在这张矩阵里，只能靠点位默认限值与曲线判据管。");

    public static readonly HelpTopic CriterionDisabled = new(
        "停用判据",
        "关掉「启用」再保存，这一条判据会被移除，阈值不再保留。",
        "只是临时不想用的话请先记下阈值：系统只留启用中的判据，历史判异也不看它。");

    public static readonly HelpTopic CurveFeatureAxis = new(
        "特征口径",
        "面积与上升/保压斜率都按采样序号轴算，不是按时间或位移。",
        "改采样点数或扫描周期后，同一波形的数值会变，历史阈值需要重新标定。");

    // ---------- 编辑用户 ----------

    public static readonly HelpTopic DisplayName = new(
        "显示名",
        "显示名只出现在用户列表里；审计日志的操作人用的是登录名。",
        "改显示名不会改变历史操作的归属；登录名创建后不可修改。");

    // ---------- 页面 → 详解的映射 ----------

    /// <summary>某一页挂了哪些详解 —— 供页头的「本页说明」入口使用。</summary>
    /// <remarks>
    /// 这份映射是唯一的一份：页面只写 HelpRoute，不自己列 topic。
    /// 传入路由相对路径（与 <see cref="DisplayLabels.PageTitle"/> 同一套写法：不带前导斜杠、小写）。
    /// </remarks>
    public static IReadOnlyList<HelpTopic> ForPage(string route)
    {
        var path = route.Split('?')[0].Trim('/').ToLowerInvariant();
        return path switch
        {
            "" => [YieldRate, TodayScope, JudgementThreeState, LimitThreeTiers, StaleData, Cadence],
            "query" => [RangeScope, MatchMode, ExportLimit, ResultCode, RecipeScope, JudgementThreeState],
            "reports" => [YieldRate, AverageYield, RecipeScope, IssueShare, LimitThreeTiers, TrendSampleLimit, Capability, SegmentAsterisk],
            "curve-baseline" => [BaselineSampleCounts, RecipeMismatch, DeviationThresholds, OnlineBaseline, CriterionDisabled, CurveFeatureAxis],
            "logs" => [LogTimeRange, LogTimestamp, LogEntityKey, LogChange, LogKeyword],
            "config/plc" => [Heartbeat, PlcEnabled, MergeGap, SimulatorBrand],
            "config/stations" => [TriggerValue, TagDataSource, BoolAddress, FirstLastStation],
            "config/recipes" => [RecipeCode, RecipeEnabled, RecipeCopy, LimitMergeRule, TargetValue, LimitEffect, CoverablePoints],
            "config/settings" => [SaveToDispatch, ScanInterval, WriteRetry, ConfigSource, Retention, MesOutbox],
            "simulate" => [SimAutoRun, SimPalletInterval, SimNgPercent, SimRunLine, SimLastWriteBack],
            "users" => [Lockout, RoleScope, DeleteUser, UserNameImmutable, DisplayName],
            "record" => [LimitThreeTiers, JudgementThreeState, LimitColumns, OverTolerance, CurvePointCount, DeviationThresholds, RecipeScope],
            _ => []
        };
    }

    /// <summary>页面级使用说明。概念 tooltip 继续使用 ForPage 的短文案，避免悬浮提示过长。</summary>
    public static IReadOnlyList<PageHelpSection> GuideForPage(string route)
    {
        var path = route.Split('?')[0].Trim('/').ToLowerInvariant();
        return path switch
        {
            "" =>
            [
                Section("页面用途",
                    "实时看板用于快速判断采集链路是否持续工作、哪些工站需要处理，以及今天的质量结果。没有工站时，先到 PLC 连接和工站配置建立采集对象；也可以用 PLC 仿真验证流程。",
                    "顶部心跳说明采集器是否仍在上报运行状态；PLC 已连接并不代表工站数据一定在持续更新。停采、心跳过期和自动仿真会有各自的状态提示。"),
                Section("看板浏览顺序",
                    "先看顶部采集状态、心跳和 PLC 异常，再看工站卡片。卡片状态按停用、故障、采集中、NG、OK 等状态表达；点位异常会突出显示。点选「本站明细」可直接检查最近一条记录。",
                    "「最近采集」是便于巡线的 12 条摘要，不是完整历史；需要按时间、托盘、工站等条件查找时进入数据查询。"),
                Section("指标与点位",
                    "今日件数按 OK、NG、未判定分开展示。直通率为 OK ÷ (OK + NG)，未判定不进分子或分母；平均节拍是各工站最近一次采集耗时的平均值。",
                    "顶部最近节拍表示距离最近一次工站完成经过的时间，与平均节拍含义不同。卡片展示 5 个以内的全部点位；点位更多时优先显示超限/预警项，普通点位可展开查看。"),
                Section("异常排查",
                    "若数据新鲜度异常，先确认采集服务是否运行，再看 PLC 连接状态、工站握手地址及最近记录的结果码和错误信息。若 PLC 正常但最近节拍变慢，也要区分产线无料、工站停用和采集链路停止。",
                    "卡片的颜色、状态图标和文字共同表达同一状态；点位预警用于提前关注，超限才进入判废判定。")
            ],
            "query" =>
            [
                Section("页面用途",
                    "数据查询包含「记录查询」和「产品追溯」两种方式：前者按日期与条件定位单条采集记录，后者按完整流水号串起同一次生产的各工站履历。",
                    "文本输入停止约 400 毫秒后会自动查询；日期和下拉条件变化也会触发查询。筛选条件、页码和页大小保存在 URL 中，可复制链接或刷新后继续查看。"),
                Section("产品追溯",
                    "输入完整流水号后，系统会在各月份运行库中精确查找，并按月份键和会话 ID 分组，展示工站顺序、采集结果、判定和时间；同一条流水号关联多个会话时必须先选择。缺少会话关联的旧记录会单独列出，无法保证履历完整。",
                    "工站履历中的缺站提示依据当前启用工站配置，历史期间若调整过路线，提示仅供参考。点采集记录的「明细」可查看完整点位、曲线及原始数据。"),
                Section("筛选与结果",
                    "起止日期都包含整日。托盘码和流水号采用包含匹配，不是前缀匹配；工站、判定、结果码及具体型号按所选值筛选。型号「全部」不限制型号，「未选型号」只找型号为空的记录。",
                    "命中数是分页前的总数，翻页不会改变它；结果按触发时间从新到旧排列。点击记录行或明细入口可打开完整快照。"),
                Section("判定与结果码",
                    "判定描述质量结论：OK、NG 或未判定；结果码描述采集握手与归档处理结果，两者不是同一字段。结果码 1 是待采集触发态，通常不会作为已完成记录展示。",
                    "读失败、校验失败或归档失败应先沿结果码和错误信息排查通讯/数据链路，不能只凭它判断产品工艺不合格。"),
                Section("导出与注意事项",
                    $"导出按当前筛选和排序取前 {SystemDefaults.ExportRowLimit:N0} 条；超过上限时页面会提示截断。需要完整导出时缩小日期范围或增加筛选条件后分批导出。",
                    "导出权限按页面角色控制。若查询不到旧记录，先确认时间范围、型号筛选和记录保留周期；月库记录被清理后无法从查询页恢复。")
            ],
            "record" =>
            [
                Section("页面用途",
                    "记录明细展示一条已采集记录的历史快照，通常从数据查询结果或看板工站卡片进入。地址中的月份和记录编号用于定位对应月库记录；记录不存在时，可能是链接错误或数据已按保留策略清理。",
                    "页面包含工站/托盘信息、判定与结果码、点位值和限值、采集曲线、波形偏离以及原始数据等区域。"),
                Section("推荐检查顺序",
                    "先看判定、结果码、错误信息和触发/完成时间；再检查异常点位的当前值、规格上下限与预警带；如果问题与动态过程有关，再展开曲线或下载数据分析。",
                    "查看同托盘历史可用于判断问题从哪一站开始出现；返回查询会保留原筛选条件。导出适合留存单条记录及其原始数据。"),
                Section("历史值与图表",
                    "点位限值随记录一起保存，表示采集当时实际使用的口径；之后修改产品型号或默认限值不会重算历史记录。必填点位读空时数值可能显示为「-」，但仍可能触发超差判定。",
                    "曲线图可能为便于显示而抽稀，点数栏反映原始采样数量；导出数据包含完整采样。若 X/Y 序列成对且长度一致，图表按 XY 绘制，否则按采样序号显示。"),
                Section("偏离与异常排查",
                    "波形基线偏离属于影子分析信息，不改变本条记录的 OK/NG 结果。基线不可用、样本不足或曲线文件读取失败时，应按页面提示区分分析能力不足与采集判定失败。",
                    "若点位值为空或结果码异常，优先查看采集错误与握手结果；不要把所有「-」都当成数值超限，也不要把曲线缺失直接等同于产品 NG。")
            ],
            "reports" =>
            [
                Section("页面用途",
                    "报表从区间数据汇总质量与过程表现，可按工站、型号和日期范围查看直通率、型号汇总、不良/预警 Top N、参数趋势及过程能力。趋势和过程能力需要再选择数值点位。",
                    "按筛选条件刷新后，各图表和表格使用对应区间；趋势与过程能力可能在首屏摘要之后加载。需要留档时，可导出有权限的趋势数据。"),
                Section("质量与 Top N",
                    "区间直通率按总 OK ÷ (总 OK + 总 NG) 计算，未判定排除；按件数加权，不是每天直通率的简单平均。不良榜统计超规格次数，预警榜单独统计预警次数。",
                    "榜单按点位名称聚合次数；不筛工站时，不同工站的同名点位可能合并。占比以区间内相应类别的总次数为分母，榜内占比未达到 100% 时，差额来自未显示项目。"),
                Section("趋势与过程能力",
                    $"趋势和过程能力单次最多处理最近 {SystemDefaults.TrendSampleLimit:N0} 个采样点；达到上限会提示数据截断，可缩短日期区间查看更早时段。规格限发生变化时，过程能力按限值分段计算，带星号的限值可能来自当前配置估计。",
                    "Cpk 反映组内波动，Ppk 反映整体波动；样本不足、没有有效规格限或过程无波动时，不应据单个指数作能力结论。页面显示的 Nelson 判异是实现的规则子集，不代表完整八条规则。"),
                Section("型号与解读边界",
                    "「全部型号」汇总可按页面的历史型号归并规则查看整体；指定一个型号编码时按该编码精确筛选，不会自动包含改码前的旧编码。比较历史改码前后的表现时，请同时检查型号汇总视图。",
                    "报表用于识别趋势和调查方向，不替代单件记录判定。看到点位异常后，可缩小区间、限定工站，再到数据查询检查具体记录与当时限值。")
            ],
            "curve-baseline" =>
            [
                Section("页面用途",
                    "波形基线页对选定工站、曲线及序列计算历史特征分布，并用它给近期曲线打偏离分。先选工站和曲线，再选序列及时间区间；页面默认值只是分析起点，不代表所有工站都使用同一基线。",
                    "本页按当前筛选即时分析；后台在线基线缓存是另一套周期更新的数据，两者状态和结果可能暂时不同。"),
                Section("样本数字怎么读",
                    "「取到」是区间内的总数，包含其他型号、NG 和未判定；「型号不匹配」单列不属于当前型号范围的样本。只有更早的本型号 OK 样本用于建模。",
                    "最近最多 30 条作为独立评估集，不参与建模；未判定样本也不进入基线或影子四格。样本不足 20 条时基线不可靠，不要把无结论当成正常。"),
                Section("偏离判定与明细",
                    "每条曲线会提取面积、峰值、位置、斜率等特征，并与中位数及 MAD 离散度形成的基线比较。综合偏离达到 2σ 可疑、达到 3σ 异常；单维达到 4σ 也会判异常，具体维度可在样本明细中展开查看。",
                    "选中一条样本后检查偏离维度和原始波形，再结合工艺变更、换刀或设备维护时间判断原因。采样点数或扫描周期变更会改变特征数值，应重新评估阈值。"),
                Section("影子模式与注意事项",
                    "页面上的检出、误报和漏报只统计具有明确 OK/NG 判定的独立评估样本；影子模式只记录分析结果，不会改变生产判定，也不会自动拦截工件。",
                    "型号不匹配样本不会参与当前型号基线。改型号编码、曲线定义或采样配置后，先确认样本归属和基线状态，再解释偏离分；后台在线基线通常每 5 分钟按最近 30 天的 OK 样本更新。")
            ],
            "logs" =>
            [
                Section("页面用途",
                    "审计日志用于追踪谁在何时对哪个对象执行了什么操作。默认不限制日期、按最新时间查看；操作筛选适合定位某类变更，关键字适合跨字段搜索。",
                    "日志存放在配置库中，与按月清理的运行记录库分开。系统运行数据的保留年数不会自动清理审计日志。"),
                Section("筛选与查看",
                    "关键字会搜索用户、动作、对象、键以及变更前后内容；动作、用户和对象下拉条件按所选项精确筛选。多个筛选条件同时生效。日期起止按整日包含，留空表示不限制该端。",
                    "点击时间表头可切换新到旧或旧到新；展开一行查看变更详情，复制按钮便于粘贴到工单或调查记录。时间记录使用服务器本地时钟，跨时区核对时先确认服务器时区。"),
                Section("变更内容怎么读",
                    "配置变更类动作通常以「旧值 → 新值」展示；新增没有旧值，登录、导出等事件则直接显示事件说明。键列指出记录针对的对象，例如工站编码或「工站/点位」组合。",
                    "审计记录用于追溯操作，不保证替代业务数据快照。调查采集值或质量结论时，应同时打开对应运行记录。"),
                Section("导出与边界",
                    "导出沿用当前筛选与排序，最多取前 5,000 条；要更完整的时间段请分段导出。没有日期条件时结果可能很多，可先限定日期或对象后再导出。",
                    "只有实际写入审计库的事件会出现；不同操作记录的变更详情粒度可能不同。分页边界若遇到相同时间戳，排序不承诺相同时间内的固定先后。")
            ],
            "config/plc" =>
            [
                Section("页面用途",
                    "PLC 连接页维护通讯端点、驱动品牌及连接级参数。先新增或选择一条连接，配置名称、品牌和网络地址，再按设备手册设置端口、超时、数据字序和可选心跳；保存后工站才能绑定这条连接。",
                    "列表会显示启停状态和引用工站数量。仍被工站引用的连接不能直接删除；需要先调整工站绑定。"),
                Section("通讯字段与品牌参数",
                    "Host、端口和驱动品牌决定连接目标；浮点字序、字符串字节序影响多字节数据的解析。Modbus 的 Extra 填站号，西门子驱动的 Extra 填 rack/slot，其他品牌按界面提示填写。",
                    "合并间隙控制相邻地址是否合并为批量读取；合并太积极可能增加单次读取跨度，合并太保守会增加通讯请求数。超时应结合设备响应速度和网络状况设置。"),
                Section("心跳与启停",
                    "心跳地址可按设备需要配置；启用后采集端周期写递增值或 0/1 翻转值，PLC 可据此判断上位机是否仍运行。使用字地址并确认该地址允许上位机写入，避免占用工艺控制寄存器。",
                    "停用连接会使其下所有工站停止采集。连接参数变更后，采集端按配置版本更新对应连接；检查看板数据新鲜度和连接错误，确认变更已生效。"),
                Section("验证与风险",
                    "保存前对照 PLC 型号、IP/主机名、端口、机架/插槽和字节序逐项核对。能连通不代表数据解析正确；浮点值异常、字符串乱码时优先核实字序与长度。",
                    "模拟器品牌的数据位于本机内存，并与仿真页共享；仿真触发会被正常采集并落库。不要将模拟器结果当成真实 PLC 生产数据。")
            ],
            "config/stations" =>
            [
                Section("页面用途与结构",
                    "工站配置以左侧工站列表为入口，右侧分为握手、点位和曲线等配置区域。没有 PLC 连接时先建立 PLC；新工站选择所属连接并设置唯一编码、名称、产线顺序和启用状态。",
                    "工站基本信息、点位、曲线、型号覆盖和波形判据由不同按钮/对话框保存。保存一类配置后，确认列表与版本提示已经更新，不要假设一次保存会提交所有打开过的编辑。"),
                Section("握手与产线拓扑",
                    "触发地址由 PLC 置为触发值后开始采集；采集端完成后把结果码写回同一寄存器。触发值不能使用 0 或结果码，否则可能上电误触发或反复触发。托盘码地址决定记录归属；有料地址留空按恒有料处理。",
                    "启用工站的顺序必须为正且不重复，首站和末站各自最多一个。首站建立托盘会话，末站关闭会话并推动结果上报；缺少首末站会造成跳站或会话未结束，应先核对拓扑再上线。"),
                Section("点位、限值与数据来源",
                    "PLC 点位填写设备地址；文件源点位填写 JSON 字段路径或 CSV 表头列名，并配置数据文件路径。数值点位可设倍率、偏移、单位、规格上下限、预警带和目标值；字符串点位要关注读取长度，布尔点位使用字地址。",
                    "规格限参与 OK/NG 判定，预警带用于提前提示而不单独判废，目标值用于中心线或分析参考。型号覆盖值按字段与点位默认值合并；保存后新的配置版本由采集端后续快照读取，已落库历史记录不会重算。"),
                Section("曲线与变更风险",
                    "曲线配置要指定序列地址、类型、单位和采样点数；XY 序列应成对设置。点数越多，读取和存储负担越高。波形判据需要绑定存在的序列并设置有效阈值；停用判据会移除该条当前规则。",
                    "删除工站会删除其点位和曲线定义，删除曲线可能使对应波形基线失效；历史记录本身不会因此重算。修改握手地址、点位来源或限值后，建议用单件仿真/试采集核对结果码、原始值和判定。")
            ],
            "config/recipes" =>
            [
                Section("页面用途",
                    "产品型号页维护型号编码、名称、启用状态和型号级限值覆盖，并选择当前生产型号。新建型号后需按需要配置覆盖值，再通过「设为当前」切换采集口径；取消当前型号则回到点位默认限值。",
                    "复制型号只复制限值字段，不复制波形基线或历史记录归属。复制后检查编码、名称和覆盖矩阵，避免把临时试制参数误用于量产型号。"),
                Section("限值覆盖规则",
                    "型号覆盖只用于数值点位。规格上限/下限、预警上限/下限和目标值逐字段合并；覆盖字段留空表示沿用点位默认值，而不是关闭该限值。覆盖与默认值合并后仍须满足上下限和预警带关系。",
                    "五个覆盖字段全部为空时该点位不保留覆盖记录。布尔、字符串点位不在覆盖矩阵中；需要调整它们的行为时应回到工站点位配置。"),
                Section("生效时间与历史",
                    "保存覆盖或切换当前型号会递增配置版本，采集端在后续获取配置快照时使用新型号/限值。变更不会重算已经采集的记录；记录明细保留当时实际限值。",
                    "停用或删除当前型号会清除当前选择，后续采集回落到点位默认限值。改编码不会重写历史记录；报表按页面视图区分全部型号汇总与单编码精确筛选。"),
                Section("上线前检查",
                    "核对编码唯一性、型号启用状态、目标型号是否已设为当前，以及覆盖后的有效值是否符合工艺要求。对一个字段留空时，务必确认继承的默认值符合该型号要求。",
                    "型号限值影响后续判定，建议在切换前确认在制品策略，并通过一条代表性记录检查实际生效限值。")
            ],
            "config/settings" =>
            [
                Section("页面用途与保存",
                    "系统设置汇总运行状态和系统级参数，包括采集扫描、写回重试、记录保留、MES 推送、备份及品牌信息。大多数配置编辑后统一点页面保存；离开页面时会提示是否放弃当前编辑。",
                    "厂名和 Logo 属于客户品牌文件，使用独立保存操作；仿真自动跑线参数在本页只读，需到 PLC 仿真页修改。带只读标识或运行时计算的数据不能在本页编辑。"),
                Section("关键参数怎么设",
                    "扫描间隔越短，采集端询问 PLC 越频繁，但不保证读数更准确；过短可能增加超时和通讯负荷。写回重试的次数是总尝试次数，0 或 1 都只写一次；间隔会延长工站完成时间。",
                    "启用 MES 推送时检查 HTTP(S) 地址、超时与网络可达性；失败记录会留在待推送队列并重试。保留年数按整月清理运行记录和曲线文件，审计日志不受该年限控制。"),
                Section("配置生效与运行影响",
                    "配置库参数保存后写入配置版本，采集端在后续读取快照时使用新值。停用采集需要先确认，再保存设置；停采期间不会继续读取工站，但已有历史记录仍可查询。",
                    "运行状态卡片显示当前配置来源、备份和 MES 积压情况。积压持续增加时先检查 MES 地址、网络和对端可用性，不要通过删除运行记录来清理队列。"),
                Section("不可逆操作与检查",
                    "缩短保留年数前仔细核对页面提示的清理月份，并先确认是否需要备份；被清理的整月运行库与曲线文件不可恢复。在线备份包不包含曲线文件，波形资料需按单独的文件管理方案留存。",
                    "客户约定的设备数量上限是提醒信息，不一定会阻止保存；检查默认口令提示和管理员权限。修改后回看页面状态及看板心跳，确认配置确实被采集端接收。")
            ],
            "simulate" =>
            [
                Section("页面用途",
                    "PLC 仿真使用本机内存模拟寄存器来验证握手、点位读取、判定、归档和整线流程，不需要连接真实设备。仿真数据会被正常采集并写入运行库，因此报表和看板会出现这些记录。",
                    "仅绑定模拟器 PLC 的启用工站参与仿真走线；生产环境自动跑线可能持续生成测试数据，投入真实生产前确认自动开关已关闭。"),
                Section("参数与运行步骤",
                    "先设置托盘间隔、NG 比例和托盘池大小，再点击保存参数；自动跑线开关是独立操作。需要单次验证时选择工站触发；需要验证首末站和托盘会话时输入托盘码并运行整线。",
                    "托盘池为 1 时每轮重复同一托盘码。NG 比例按托盘抽取随机结果，单托盘最多挑一个工站注入异常；没有启用且带数值规格限的点位时，该轮未必能产生 NG。"),
                Section("回写与状态",
                    "触发后观察当前托盘、当前工站、最近回写码和完成计数。回写码 2 表示采集成功，其他码表示采集/归档过程中的相应问题；它描述采集端响应，不是 PLC 自己的质量判定。",
                    "整线按顺序逐站触发并等待采集端回写，每站等待有超时上限。页面显示未走完时，查看停在哪一站及回写码，再核对采集服务、PLC/工站启用状态和握手地址。"),
                Section("安全边界",
                    "仿真开关会立即改变后台运行状态，参数则需单独保存；两者不是同一次提交。开启自动跑线前确认不会把测试记录混入真实生产报表，必要时先停采或在测试环境操作。",
                    "单站触发非首站可能形成跳站异常；它适用于局部验证，不等同于完整托盘流程。测试结束后关闭自动跑线并用托盘码或时间范围筛除测试数据。")
            ],
            "users" =>
            [
                Section("页面用途与权限",
                    "用户页仅供管理员管理账号、角色、显示名、锁定和密码。新建账号后选择至少一个角色；已有账号可调整显示名和角色、解除锁定或重置密码。",
                    "角色变更通常在目标用户下次登录后体现。撤销自己最后的管理员权限会立即影响后续管理能力，操作前确认至少仍有其他管理员。"),
                Section("账号与密码规则",
                    "用户名是登录名，创建后不可修改；创建时核对拼写和唯一性。显示名用于界面识别，不改变历史审计日志里的登录名。",
                    "初始密码和重置密码遵循系统密码策略，页面会显示校验结果。重置成功后通过安全渠道通知用户，不要把密码写入备注或审计说明。"),
                Section("锁定、重置与删除",
                    "连续登录失败达到系统阈值后账号会暂时锁定；等待锁定期结束或管理员解除锁定。重置密码与解除锁定是独立操作，处理前先确认账号身份。",
                    "删除当前登录账号被禁止，系统也会阻止删除最后一个管理员。删除其他账号不可逆；离职或临时停用优先评估角色调整和锁定，避免丢失审计追溯对象。"),
                Section("操作结果与审计",
                    "用户创建、角色调整、密码重置、解锁和删除会分别执行并记录审计；创建账号与分配角色是分步操作，界面提示部分失败时要回列表核对实际账号状态。",
                    "审计日志记录操作者、对象和操作结果，不保存明文密码。权限调整后可让用户重新登录，再按实际页面入口确认授权范围。")
            ],
            _ => []
        };
    }

    private static PageHelpSection Section(string title, params string[] paragraphs)
        => new(title, paragraphs);
}
