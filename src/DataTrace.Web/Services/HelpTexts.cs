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
        "与「数据查询」的「今天」预设同一口径，两处数字应该一致。");

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
        "采集握手码：1 = 触发待采集，2 = 采集成功，3~8 是各类异常（读失败、写库失败等）。",
        "它与限值判定相互独立：见到 3/6/7 要查通讯与数据库，不是工艺问题。");

    public static readonly HelpTopic RecipeScope = new(
        "型号口径",
        "记录按采集当时的型号落库；「未选型号」指那批没绑型号的记录。",
        "型号改码后旧编码仍算本型号，报表按同一口径合并，不会漏掉历史样本。");

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
        "改动即时生效；撤掉某人的管理员角色时，注意别把最后一个管理员撤掉。");

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
        "触发与回写共用同一寄存器：1 = 触发，2–8 是采集端写回的结果码。",
        "填 0 会在复位或上电时误触发；填 2–8 则回写后仍等于触发值，每个扫描周期都会重复采一次。");

    public static readonly HelpTopic BoolAddress = new(
        "布尔点位",
        "Bool 点位要填字地址（如 D100、MW10），判定规则是该字非 0 即为真。",
        "填位地址（M100、DB1.DBX0.0）不报错，但读取计划只收字地址、会被静默丢掉，表现为值恒为 0。");

    public static readonly HelpTopic PositionScope = new(
        "点位归属",
        "工站级（位置 0）每轮都读；产品位（位置 1）属于托盘上的工件，读到无料时该位点位不读。",
        "工站级点位一旦超规格限，整条工站记录判 NG；产品位只影响该工件那一位。");

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
        "型号编码",
        "只允许字母、数字、连字符与下划线。",
        "逗号必须排除：历史编码是逗号分隔串，编码自带逗号会让认领口径被拆散，基线混入别的型号样本。");

    public static readonly HelpTopic RecipeEnabled = new(
        "型号启用",
        "型号未选择或已被停用时，采集一律回落到点位自带的默认限值。",
        "停用的型号不能再设为当前；停用当前型号之前会先确认。");

    public static readonly HelpTopic RecipeCopy = new(
        "复制型号",
        "只复制限值字段，不继承波形基线，也不带上历史编码。",
        "新编码的基线要从零攒够样本才可用，历史记录也不会挂到新型号上。");

    // ---------- 波形基线 ----------

    public static readonly HelpTopic BaselineSampleCounts = new(
        "样本口径",
        "「取到」是区间内本型号的真实条数；建立基线只用其中的合格样本。",
        "「参与打分」是最近 30 条逐条算偏离（含 NG），所以三个数字对不上是正常的。");

    public static readonly HelpTopic RecipeMismatch = new(
        "型号不匹配",
        "区间内按采集当时的型号统计、不属于本型号的样本数。",
        "改码后旧编码仍算本型号；未选型号时只有型号为空的那些记录才算本型号。");

    public static readonly HelpTopic DeviationThresholds = new(
        "偏离门槛",
        "综合偏离是各维度 z 值的均方根，明细里只列出偏离 ≥ 2σ 的维度。",
        "综合 ≥ 3σ 或单维 ≥ 4σ 判异常，综合 ≥ 2σ 判可疑；零波动维度被打破也会计入。");

    public static readonly HelpTopic OnlineBaseline = new(
        "在线基线",
        "后台每 5 分钟用最近 30 天的合格样本重建一次，样本不足 20 条的序列不入缓存。",
        "本页是按所选区间现算的，两者互不影响；服务刚启动的空窗期里，新记录没有偏离分。");

    // ---------- 审计日志 ----------

    public static readonly HelpTopic LogTimeRange = new(
        "时间范围",
        "「近 7 天」是从此刻往前推 168 小时，不是自然日。",
        "它与数据查询页的「近 7 天」（按自然日）口径不同，两处查出来的条数可以不等。");

    public static readonly HelpTopic LogEntityKey = new(
        "键列",
        "「键」指向这条日志针对谁，由写入方决定：工站写编码，点位写「工站码/点位码」。",
        "同一个人改不同对象时靠这一列区分；读不懂时配合「对象」列一起看。");

    public static readonly HelpTopic LogChange = new(
        "变更列",
        "只有变更类动作才渲染「旧 → 新」；左侧显示「新增」表示这件事本来就没有旧值。",
        "登录、退出、登录失败、解除锁定、导出属于「发生了一件事」，说明直接写在这一列。");

    public static readonly HelpTopic LogKeyword = new(
        "关键字",
        "包含匹配，一次搜用户、动作、对象、键与变更前后共六个字段。",
        "中文也能搜到：命中动作或对象的中文名时，会连同对应动作码一起查（「删除」能搜到 Delete）。");

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
        "显示采集端写回触发寄存器的响应码：2 是采集成功，3–8 分别是读失败、托盘码非法、校验失败等。",
        "它不是 PLC 写的值；仿真等不到这个回写（寄存器一直等于触发值）就判超时。");

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
            "curve-baseline" => [BaselineSampleCounts, RecipeMismatch, DeviationThresholds, OnlineBaseline],
            "logs" => [LogTimeRange, LogEntityKey, LogChange, LogKeyword],
            "config/plc" => [Heartbeat, PlcEnabled, MergeGap, SimulatorBrand],
            "config/stations" => [TriggerValue, BoolAddress, PositionScope, FirstLastStation],
            "config/recipes" => [RecipeCode, RecipeEnabled, RecipeCopy],
            "config/settings" => [SaveToDispatch, ScanInterval, WriteRetry, ConfigSource, Retention, MesOutbox],
            "simulate" => [SimAutoRun, SimPalletInterval, SimNgPercent, SimRunLine, SimLastWriteBack],
            "users" => [Lockout, RoleScope, DeleteUser, UserNameImmutable],
            _ => []
        };
    }
}
