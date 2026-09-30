using System.Diagnostics;
using System.Text.Json;
using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Planning;
using DataTrace.Plc.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

/// <summary>
/// 试读的实现：复用采集链路现成的三件套（读计划编译、字块读取、解码与限值判定），
/// 在进入任何副作用之前返回。
/// </summary>
/// <remarks>
/// 两处刻意的偏离，都是为了"配置验证"这个目的：
/// <list type="number">
/// <item>不经 <see cref="CollectEvaluator"/> —— 它在产品位空位时会跳过该位点位，
/// 而试读要看到"我配的每个点位都读到了什么"，空位也要读出来。</item>
/// <item>文件源点位自己读文件，<b>不复用 <see cref="FileSourceReader"/></b> ——
/// 后者一定先归档再解析，而归档本身就是副作用。</item>
/// </list>
/// 解码与判定仍走同一套 <see cref="ValueCodec"/> 与 <see cref="LimitEvaluator"/>，与真采口径一致。
/// </remarks>
public sealed class StationTrialReader : IStationTrialReader
{
    /// <summary>单次试读上限。PLC 未连接时会先走重连（3 次 × 2 秒），必须有个头。</summary>
    private const int TimeoutMs = 10_000;

    /// <summary>原始字最多显示几个，超出省略 —— 曲线块动辄上千字，全铺出来只会淹没页面。</summary>
    private const int MaxRawWords = 8;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlcQueueAccess _queues;
    private readonly ILogger<StationTrialReader> _logger;

    public StationTrialReader(
        IServiceScopeFactory scopeFactory,
        IPlcQueueAccess queues,
        ILogger<StationTrialReader> logger)
    {
        _scopeFactory = scopeFactory;
        _queues = queues;
        _logger = logger;
    }

    public string? UnavailableReason(int plcConnectionId)
    {
        if (!_queues.TryGetTrialQueue(plcConnectionId, out var queue, out var reason))
        {
            return reason;
        }

        // 冷却期内队列会立刻失败，不如提前说清楚，省得用户点了按钮等一个必然的失败。
        return queue.IsCoolingDown
            ? "PLC 处于断线冷却期（刚怀疑断过线），冷却结束后再试读。"
            : null;
    }

    public async Task<StationTrialResult> ReadAsync(
        Station station,
        PlcConnection connection,
        CancellationToken cancellationToken = default)
    {
        var at = DateTime.Now;
        var sw = Stopwatch.StartNew();

        if (!_queues.TryGetTrialQueue(connection.Id, out var queue, out var reason))
        {
            return Failed(at, sw, reason ?? "该工站的 PLC 连接不可用。");
        }

        if (queue.IsCoolingDown)
        {
            return Failed(at, sw, "PLC 处于断线冷却期，暂不能试读。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutMs);

        // 地址非法在 Compile 里抛：这正是试读最该抓的错，如实报出来，不让页面崩。
        StationAcquisitionPlan plan;
        try
        {
            plan = StationAcquisitionPlanner.Compile(station, connection, queue.Driver);
        }
        catch (PlcDriverException ex)
        {
            _logger.LogWarning(ex, "工站 {Station} 试读：读计划无法编译", station.Code);
            return Failed(at, sw, ex.Message);
        }

        ushort[][] buffers;
        try
        {
            buffers = await StationBlockReader.ReadAsync(plan, queue, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Failed(at, sw, $"试读超时（超过 {TimeoutMs / 1000} 秒）。PLC 可能未连接或链路不稳。");
        }
        catch (Exception ex) when (ex is PlcDriverException or InvalidOperationException)
        {
            return Failed(at, sw, $"PLC 读取失败：{ex.Message}");
        }

        var trigger = await ReadTriggerAsync(station, queue, timeout.Token).ConfigureAwait(false);
        var palletCode = TryPalletCode(station, connection, plan.Plan, buffers);
        var occupied = TryOccupied(plan.Plan, buffers);
        var (fileValues, fileError) = ReadFileValues(station);

        // 取不到配置快照不算失败：回落到点位默认限值，试读的其余结论照常给。
        var config = await SnapshotAsync(timeout.Token).ConfigureAwait(false);
        var recipe = config is null ? null : SessionRecipe.Select(config, station.IsFirstStation, frozenCode: null);

        var tags = BuildTags(station, connection, plan.Plan, buffers, fileValues, recipe);
        var curves = BuildCurves(station, plan.Plan, buffers);

        sw.Stop();
        return new StationTrialResult(
            at,
            sw.ElapsedMilliseconds,
            plan.Plan.Blocks.Count,
            plan.Plan.Blocks.Sum(b => b.WordCount),
            trigger,
            palletCode,
            occupied,
            tags,
            curves,
            fileError);
    }

    /// <summary>
    /// 触发地址单独读一个字。它不进读计划（采集只在扫描循环里读它），
    /// 但"当前触发值 vs 配置期望值"恰恰是地址配错时最直接的证据。
    /// </summary>
    private async Task<StationTrialTrigger> ReadTriggerAsync(
        Station station,
        PlcRequestQueue queue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(station.TriggerAddress))
        {
            return new StationTrialTrigger("", station.TriggerValue, null, false, "未配置触发地址");
        }

        if (!queue.Driver.TryParseAddress(station.TriggerAddress, out var address))
        {
            return new StationTrialTrigger(station.TriggerAddress, station.TriggerValue, null, false, "触发地址无法解析");
        }

        if (address.IsBit)
        {
            return new StationTrialTrigger(station.TriggerAddress, station.TriggerValue, null, false, "触发地址是位地址，读取只支持字地址");
        }

        try
        {
            var words = await queue.ReadWordsAsync(address, 1, cancellationToken).ConfigureAwait(false);
            if (words.Length == 0)
            {
                return new StationTrialTrigger(station.TriggerAddress, station.TriggerValue, null, false, "读到空");
            }

            var value = words[0];
            return new StationTrialTrigger(
                station.TriggerAddress,
                station.TriggerValue,
                value,
                (short)value == station.TriggerValue,
                null);
        }
        catch (Exception ex) when (ex is PlcDriverException or InvalidOperationException)
        {
            return new StationTrialTrigger(station.TriggerAddress, station.TriggerValue, null, false, ex.Message);
        }
    }

    /// <summary>
    /// 安全取字。
    /// </summary>
    /// <remarks>
    /// 驱动返回的块可能比请求的短（PLC 断帧、越界），而 <see cref="ReadPlan.GetWords"/> 在这种情况下
    /// 会直接抛（它用 Array.Copy，不检查源长度）。试读必须把这变成"这一项没读齐"，
    /// 而不是让整次试读连带失败 —— 别的点位的信息仍然有价值。
    /// </remarks>
    private static bool TryGetWords(
        ReadPlan plan,
        IReadOnlyList<ushort[]> buffers,
        string key,
        out ushort[] words)
    {
        words = [];
        if (!plan.Items.TryGetValue(key, out var slices))
        {
            return false;
        }

        foreach (var slice in slices)
        {
            if (slice.BlockIndex >= buffers.Count
                || buffers[slice.BlockIndex].Length < slice.OffsetInBlock + slice.WordCount)
            {
                return false;
            }
        }

        words = plan.GetWords(key, buffers);
        return true;
    }

    /// <summary>产品位有没有料。没配有料地址时返回 null（约定为恒为有料）。</summary>
    private static bool? TryOccupied(ReadPlan plan, IReadOnlyList<ushort[]> buffers)
    {
        if (!plan.Items.ContainsKey("occ_1"))
        {
            return null;
        }

        return TryGetWords(plan, buffers, "occ_1", out var words) && words.Length > 0 && words[0] != 0;
    }

    private static string? TryPalletCode(
        Station station,
        PlcConnection connection,
        ReadPlan plan,
        ushort[][] buffers)
    {
        try
        {
            var code = StationSampleDecoder.PalletCode(station, connection, plan, buffers);
            return string.IsNullOrWhiteSpace(code) ? null : code;
        }
        catch (Exception)
        {
            // 托盘码解不出来不该拖垮整次试读：其余点位的信息仍有价值。
            return null;
        }
    }

    private static List<StationTrialTag> BuildTags(
        Station station,
        PlcConnection connection,
        ReadPlan plan,
        IReadOnlyList<ushort[]> buffers,
        IReadOnlyDictionary<int, SourceTagValue> fileValues,
        Recipe? recipe)
    {
        var result = new List<StationTrialTag>();
        foreach (var tag in station.Tags.Where(t => t.Enabled))
        {
            var fromFile = tag.Source == TagDataSource.JsonFile;
            string rawText;
            string? text = null;
            double? numeric = null;
            bool failed;

            if (fromFile)
            {
                // 文件源没有"原始字"这回事；字段名已经在地址列里，这里留空避免重复。
                rawText = "";
                if (fileValues.TryGetValue(tag.Id, out var source))
                {
                    text = source.Text;
                    numeric = source.Numeric;
                }

                failed = tag.DataType == PlcDataType.String
                    ? string.IsNullOrWhiteSpace(text)
                    : numeric is null;
            }
            else
            {
                // 块短了也算没读到：完整数据拿不到，就不该拿半截去解码。
                var read = TryGetWords(plan, buffers, $"tag_{tag.Id}", out var words);
                failed = !read || words.Length == 0;
                rawText = failed
                    ? ""
                    : string.Join(' ', words.Take(MaxRawWords).Select(w => $"0x{w:X4}"))
                      + (words.Length > MaxRawWords ? $" …（共 {words.Length} 字）" : "");

                if (!failed)
                {
                    if (tag.DataType == PlcDataType.String)
                    {
                        text = ValueCodec.DecodeAscii(words, tag.Length, connection.StringHighByteFirst);
                    }
                    else
                    {
                        numeric = ValueCodec.DecodeNumeric(
                            words, tag.DataType, connection.FloatWordOrder, tag.Scale, tag.Offset);
                    }
                }
            }

            var limits = RecipeLimitResolver.Resolve(tag, recipe);

            // 字符串点位不判限（与真采一致）；"没读到"也不算"超限" —— 那是两件事。
            var status = failed || tag.DataType == PlcDataType.String
                ? LimitStatus.None
                : LimitEvaluator.Evaluate(limits, numeric, tag.IsRequired);

            result.Add(new StationTrialTag(
                tag.Name,
                tag.Address,
                fromFile,
                rawText,
                failed ? "—" : Display(tag.DataType == PlcDataType.String ? text : null, numeric, tag.Unit),
                failed,
                status == LimitStatus.OutOfSpec,
                status == LimitStatus.Warning,
                DescribeLimits(limits)));
        }

        return result;
    }

    private static List<StationTrialCurve> BuildCurves(
        Station station,
        ReadPlan plan,
        IReadOnlyList<ushort[]> buffers)
    {
        var result = new List<StationTrialCurve>();
        foreach (var curve in station.Curves.Where(c => c.Enabled && c.PointCount > 0))
        {
            var series = curve.Series.ToList();
            var readOk = true;
            foreach (var item in series)
            {
                var typeWords = ValueCodec.WordCountOf(item.DataType);
                var stride = Math.Max(typeWords, item.StrideWords);
                var expected = (curve.PointCount - 1) * stride + typeWords;
                if (!TryGetWords(plan, buffers, $"curve_{curve.Id}_{item.Id}", out var words)
                    || words.Length < expected)
                {
                    readOk = false;
                    break;
                }
            }

            result.Add(new StationTrialCurve(
                curve.Name,
                series.FirstOrDefault()?.StartAddress ?? "",
                curve.PointCount,
                series.Count,
                readOk,
                readOk
                    ? $"{curve.PointCount} 点 · {series.Count} 序列读齐"
                    : $"{curve.PointCount} 点未读齐（字块不足）"));
        }

        return result;
    }

    /// <summary>
    /// 文件源点位的只读解析。
    /// </summary>
    /// <remarks>
    /// 刻意不复用 <see cref="FileSourceReader"/>：它会先把原始文件写进归档目录再解析，
    /// 而归档是副作用（试读承诺不落任何东西）。这里只复用它的两个纯解析器。
    /// </remarks>
    private static (IReadOnlyDictionary<int, SourceTagValue> Values, string? Error) ReadFileValues(Station station)
    {
        var fileTags = station.Tags.Where(t => t.Enabled && t.Source == TagDataSource.JsonFile).ToList();
        if (fileTags.Count == 0)
        {
            return (new Dictionary<int, SourceTagValue>(), null);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(station.DataFilePath);
        }
        catch (Exception ex)
        {
            return (new Dictionary<int, SourceTagValue>(), $"数据文件读取失败：{ex.Message}");
        }

        var values = new Dictionary<int, SourceTagValue>();
        if (station.DataFileFormat == DataFileFormat.Csv)
        {
            if (!CsvFieldReader.TryParse(bytes, out var row, out var error))
            {
                return (values, $"数据文件不是合法 CSV：{error}");
            }

            foreach (var tag in fileTags)
            {
                if (tag.DataType == PlcDataType.String)
                {
                    if (CsvFieldReader.TryReadText(row, tag.Address, out var text))
                    {
                        values[tag.Id] = new SourceTagValue(null, text);
                    }
                }
                else if (CsvFieldReader.TryReadNumeric(row, tag.Address, out var numeric))
                {
                    values[tag.Id] = new SourceTagValue(numeric, null);
                }
            }

            return (values, null);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            foreach (var tag in fileTags)
            {
                if (tag.DataType == PlcDataType.String)
                {
                    if (JsonFieldReader.TryReadText(document.RootElement, tag.Address, out var text))
                    {
                        values[tag.Id] = new SourceTagValue(null, text);
                    }
                }
                else if (JsonFieldReader.TryReadNumeric(document.RootElement, tag.Address, out var numeric))
                {
                    values[tag.Id] = new SourceTagValue(numeric, null);
                }
            }
        }
        catch (JsonException ex)
        {
            return (values, $"数据文件不是合法 JSON：{ex.Message}");
        }

        return (values, null);
    }

    /// <summary>取当前配置快照，只为了拿生效型号（限值覆盖来自型号）。取不到就回落到点位默认限值。</summary>
    private async Task<AppConfigurationSnapshot?> SnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            return await repo.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "试读读取配置快照失败，改用点位默认限值");
            return null;
        }
    }

    private static string Display(string? text, double? numeric, string? unit)
    {
        if (text is not null)
        {
            return string.IsNullOrWhiteSpace(text) ? "（空）" : text;
        }

        if (numeric is not double value)
        {
            return "—";
        }

        return string.IsNullOrWhiteSpace(unit) ? value.ToString("0.###") : $"{value.ToString("0.###")} {unit}";
    }

    private static string DescribeLimits(TagLimits limits)
    {
        if (!limits.HasAny)
        {
            return "不判定";
        }

        static string Number(double value) => value.ToString("0.###");
        return (limits.Lower, limits.Upper) switch
        {
            (double lower, double upper) => $"{Number(lower)}–{Number(upper)}",
            (double lower, null) => $"≥ {Number(lower)}",
            (null, double upper) => $"≤ {Number(upper)}",
            _ => "不判定"
        };
    }

    private static StationTrialResult Failed(DateTime at, Stopwatch sw, string error)
    {
        sw.Stop();
        return new StationTrialResult(
            at,
            sw.ElapsedMilliseconds,
            0,
            0,
            new StationTrialTrigger("", 0, null, false, null),
            null,
            null,
            [],
            [],
            error);
    }
}
