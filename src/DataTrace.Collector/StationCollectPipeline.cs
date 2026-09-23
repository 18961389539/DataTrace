using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Mes;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Planning;
using DataTrace.Plc.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace DataTrace.Collector;

public sealed class StationCollectPipeline
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRuntimeStatusHub _status;
    private readonly ICollectEventBus _events;
    private readonly ICurveBaselineCache _baselines;
    private readonly ILogger<StationCollectPipeline> _logger;

    public StationCollectPipeline(
        IServiceScopeFactory scopeFactory,
        IRuntimeStatusHub status,
        ICollectEventBus events,
        ICurveBaselineCache baselines,
        ILogger<StationCollectPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _status = status;
        _events = events;
        _baselines = baselines;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        Station station,
        PlcConnection connection,
        AppConfigurationSnapshot config,
        PlcRequestQueue queue,
        CancellationToken cancellationToken)
    {
        var triggerTime = DateTime.Now;
        var sw = Stopwatch.StartNew();
        short resultCode = ResultCodes.InternalError;
        string? error = null;
        CollectOutcome? outcome = null;
        CollectRecord? saved = null;

        SetStatus(station, StationRuntimeState.Busy, null, null, null, Judgement.None, null, null);

        try
        {
            outcome = await CollectAndSaveAsync(station, connection, config, queue, triggerTime, cancellationToken)
                .ConfigureAwait(false);
            saved = outcome.Record;
            resultCode = saved.ResultCode;
            error = saved.ErrorMessage;
        }
        catch (PlcDriverException ex)
        {
            resultCode = ResultCodes.PlcReadFailed;
            error = ex.Message;
            _logger.LogError(ex, "工站 {Station} PLC 通讯失败", station.Code);
        }
        catch (Exception ex)
        {
            resultCode = ResultCodes.InternalError;
            error = ex.Message;
            _logger.LogError(ex, "工站 {Station} 采集内部异常", station.Code);
        }
        finally
        {
            await WriteResultAsync(station, connection, queue, resultCode, config.Settings, cancellationToken)
                .ConfigureAwait(false);
            if (saved is not null)
            {
                saved.DurationMs = (int)sw.ElapsedMilliseconds;
            }

            SetStatus(
                station,
                resultCode is ResultCodes.PlcReadFailed or ResultCodes.InternalError
                    ? StationRuntimeState.Fault
                    : StationRuntimeState.Idle,
                saved?.PalletCode,
                saved?.SerialNo,
                resultCode,
                saved?.Judgement ?? Judgement.None,
                error,
                saved?.DurationMs,
                outcome?.Tags,
                outcome?.Curves,
                outcome?.MonthKey,
                saved?.Id);
            if (saved is not null)
            {
                _events.Publish(saved);
            }
        }
    }

    private async Task<CollectOutcome> CollectAndSaveAsync(
        Station station,
        PlcConnection connection,
        AppConfigurationSnapshot config,
        PlcRequestQueue queue,
        DateTime triggerTime,
        CancellationToken cancellationToken)
    {
        if (!queue.Driver.TryParseAddress(station.PalletCodeAddress, out var palletAddress))
        {
            throw new PlcDriverException($"托盘码地址非法: {station.PalletCodeAddress}");
        }

        var requests = new List<AddressReadRequest>
        {
            new()
            {
                Key = "pallet",
                Address = palletAddress,
                WordCount = ValueCodec.WordCountOf(station.PalletCodeDataType, station.PalletCodeLength)
            }
        };

        foreach (var pos in station.Positions.Where(p => !string.IsNullOrWhiteSpace(p.OccupiedAddress)))
        {
            if (!queue.Driver.TryParseAddress(pos.OccupiedAddress!, out var occ))
            {
                throw new PlcDriverException($"有料地址非法: {pos.OccupiedAddress}");
            }

            requests.Add(new AddressReadRequest { Key = $"occ_{pos.Index}", Address = occ, WordCount = 1 });
        }

        foreach (var tag in station.Tags.Where(t => t.Enabled))
        {
            if (!queue.Driver.TryParseAddress(tag.Address, out var addr))
            {
                throw new PlcDriverException($"点位地址非法: {tag.Address}");
            }

            requests.Add(new AddressReadRequest
            {
                Key = $"tag_{tag.Id}",
                Address = addr,
                WordCount = ValueCodec.WordCountOf(tag.DataType, tag.Length)
            });
        }

        foreach (var curve in station.Curves.Where(c => c.Enabled && c.PointCount > 0))
        {
            foreach (var series in curve.Series)
            {
                if (!queue.Driver.TryParseAddress(series.StartAddress, out var addr))
                {
                    throw new PlcDriverException($"曲线地址非法: {series.StartAddress}");
                }

                var typeWords = ValueCodec.WordCountOf(series.DataType);
                var stride = Math.Max(typeWords, series.StrideWords);
                var wordCount = (curve.PointCount - 1) * stride + typeWords;
                requests.Add(new AddressReadRequest
                {
                    Key = $"curve_{curve.Id}_{series.Id}",
                    Address = addr,
                    WordCount = wordCount
                });
            }
        }

        var plan = ReadPlanBuilder.Build(requests, queue.Driver.Capabilities.MaxWordsPerRead, connection.MergeGapWords);
        var buffers = new ushort[plan.Blocks.Count][];
        for (var i = 0; i < plan.Blocks.Count; i++)
        {
            var block = plan.Blocks[i];
            var start = new PlcAddress(block.Area, block.StartOffset, -1, AddressKind.Word, $"{block.Area}{block.StartOffset}");
            buffers[i] = await queue.ReadWordsAsync(start, block.WordCount, cancellationToken).ConfigureAwait(false);
        }

        var palletWords = plan.GetWords("pallet", buffers);
        var palletCode = station.PalletCodeDataType == PlcDataType.String
            ? ValueCodec.DecodeAscii(palletWords, station.PalletCodeLength, connection.StringHighByteFirst)
            : ((int)ValueCodec.DecodeNumeric(palletWords, station.PalletCodeDataType, connection.FloatWordOrder, 1, 0)).ToString();

        if (!PalletCodeValidator.IsValid(palletCode, out var palletError))
        {
            return Live(station, new CollectRecord
            {
                PalletCode = palletCode,
                StationId = station.Id,
                StationCode = station.Code,
                TriggerTime = triggerTime,
                CompleteTime = DateTime.Now,
                ResultCode = ResultCodes.InvalidPalletCode,
                Judgement = Judgement.Ng,
                ErrorMessage = palletError,
                RecipeCode = RecipeCode(config)
            });
        }

        var occupied = new Dictionary<int, bool>();
        for (var i = 1; i <= station.PositionCount; i++)
        {
            var key = $"occ_{i}";
            if (plan.Items.ContainsKey(key))
            {
                var w = plan.GetWords(key, buffers);
                occupied[i] = w.Length > 0 && w[0] != 0;
            }
            else
            {
                occupied[i] = true;
            }
        }

        var tagValues = new List<TagValue>();
        var products = new List<ProductRecord>();
        var curveWrites = new List<CurvePayloadWrite>();
        var validationError = false;

        for (var pos = 0; pos <= station.PositionCount; pos++)
        {
            if (pos > 0 && occupied.TryGetValue(pos, out var isOcc) && !isOcc)
            {
                products.Add(new ProductRecord { PositionIndex = pos, Occupied = false, Judgement = Judgement.None });
                continue;
            }

            var posTags = station.Tags.Where(t => t.Enabled && t.PositionIndex == pos).ToList();
            var posJudgements = new List<Judgement>();
            string? ngReason = null;

            foreach (var tag in posTags)
            {
                var words = plan.GetWords($"tag_{tag.Id}", buffers);
                double? numeric = null;
                string? text = null;
                if (tag.DataType == PlcDataType.String)
                {
                    text = ValueCodec.DecodeAscii(words, tag.Length, connection.StringHighByteFirst);
                    if (tag.IsRequired && string.IsNullOrWhiteSpace(text))
                    {
                        validationError = true;
                    }
                }
                else
                {
                    numeric = ValueCodec.DecodeNumeric(words, tag.DataType, connection.FloatWordOrder, tag.Scale, tag.Offset);
                    if (tag.IsRequired && words.Length == 0)
                    {
                        validationError = true;
                    }
                }

                // 字符串点位没有数值限值：它的必填校验已在上面按空文本处理。
                // （此前对必填字符串点位会把 null 当"取空"判超限，等于必填字符串永远判废。）
                // 生效限值 = 点位默认限值 + 当前产品型号的覆盖，判定必须走生效限值。
                var limits = RecipeLimitResolver.Resolve(tag, config.ActiveRecipe);
                var status = tag.DataType == PlcDataType.String
                    ? LimitStatus.None
                    : LimitEvaluator.Evaluate(limits, numeric, tag.IsRequired);
                var outOfLimit = status == LimitStatus.OutOfSpec;
                if (outOfLimit)
                {
                    validationError = true;
                    posJudgements.Add(Judgement.Ng);
                    ngReason ??= $"{tag.Name}超限";
                }
                else if (limits.HasAny)
                {
                    // 落在预警带也走这一支：黄区只提示，不改变合格判定与 PLC 响应码。
                    posJudgements.Add(Judgement.Ok);
                }

                tagValues.Add(new TagValue
                {
                    TagId = tag.Id,
                    TagCode = tag.Code,
                    TagName = tag.Name,
                    PositionIndex = pos,
                    DataType = tag.DataType,
                    NumericValue = numeric,
                    TextValue = text,
                    IsOutOfLimit = outOfLimit,
                    IsWarning = status == LimitStatus.Warning,
                    // 把判定用的这套限值一起存下来。存的是解析后的配置值而不是
                    // LimitEvaluator 内部收敛过的值：界面对"黄线必须在红线内侧"有三级校验，
                    // 正常配置下两者相同；万一有历史脏配置，存配置原值才能解释现场看到的东西。
                    LowerLimit = limits.Lower,
                    UpperLimit = limits.Upper,
                    WarningLowerLimit = limits.WarningLower,
                    WarningUpperLimit = limits.WarningUpper
                });
            }

            foreach (var curve in station.Curves.Where(c => c.Enabled && c.PositionIndex == pos))
            {
                var seriesPayloads = new List<CurveSeriesPayload>();
                var featureRows = new List<CurveFeature>();
                var primarySeries = CurveCriterionEvaluator.PrimarySeries(curve);
                foreach (var series in curve.Series)
                {
                    var words = plan.GetWords($"curve_{curve.Id}_{series.Id}", buffers);
                    var typeWords = ValueCodec.WordCountOf(series.DataType);
                    var stride = Math.Max(typeWords, series.StrideWords);
                    var values = new float[curve.PointCount];
                    for (var i = 0; i < curve.PointCount; i++)
                    {
                        var offset = i * stride;
                        if (offset + typeWords > words.Length)
                        {
                            validationError = true;
                            break;
                        }

                        values[i] = (float)ValueCodec.DecodeNumeric(
                            words.AsSpan(offset, typeWords).ToArray(),
                            series.DataType,
                            connection.FloatWordOrder,
                            series.Scale,
                            series.Offset);
                    }

                    // 特征与 payload 共用同一份采样值，就地算完。
                    // 波形信息从此不再只存在于外置二进制文件里，可被 SQL 聚合与后续 SPC 复用。
                    var features = CurveFeatureExtractor.Extract(values);
                    var featureRow = CurveFeatureExtractor.ToEntity(series.Name, series.Role, features);

                    // 影子模式：用预先建好的基线给这条波形打分，只写偏离字段。
                    ApplyBaselineDeviation(featureRow, curve.Id, config);
                    featureRows.Add(featureRow);

                    foreach (var criterion in curve.Criteria)
                    {
                        if (!CurveCriterionEvaluator.AppliesTo(criterion, series, primarySeries))
                        {
                            continue;
                        }

                        var reasons = CurveCriterionEvaluator.Evaluate(criterion, features);
                        if (reasons.Count == 0)
                        {
                            continue;
                        }

                        // 点位限值先于曲线判据处理，因此波形原因只在没有点位原因时才成为首因。
                        validationError = true;
                        posJudgements.Add(Judgement.Ng);
                        ngReason ??= $"{curve.Name}波形异常：{reasons[0]}";
                    }

                    seriesPayloads.Add(new CurveSeriesPayload
                    {
                        Name = series.Name,
                        Role = series.Role,
                        Values = values
                    });
                }

                if (curve.PointCount <= 0)
                {
                    validationError = true;
                }

                curveWrites.Add(new CurvePayloadWrite
                {
                    Record = new CurveRecord
                    {
                        CurveDefinitionId = curve.Id,
                        CurveCode = curve.Code,
                        CurveName = curve.Name,
                        PositionIndex = pos,
                        PointCount = curve.PointCount
                    },
                    Payload = new CurvePayload { PointCount = curve.PointCount, Series = seriesPayloads },
                    Features = featureRows
                });
            }

            if (pos > 0)
            {
                products.Add(new ProductRecord
                {
                    PositionIndex = pos,
                    Occupied = true,
                    Judgement = LimitEvaluator.Combine(posJudgements),
                    NgReason = ngReason
                });
            }
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
        var serials = scope.ServiceProvider.GetRequiredService<ISerialNumberGenerator>();
        var runtime = scope.ServiceProvider.GetRequiredService<IRuntimeStore>();
        var mes = scope.ServiceProvider.GetRequiredService<IMesPublisher>();

        string serialNo;
        string monthKey;
        PalletSession? upsertSession = null;
        ActiveSessionIndex? active = null;
        long existingSessionId = 0;
        var close = false;
        var removeActive = false;
        var processAbnormal = false;
        string? processError = null;

        if (station.IsFirstStation)
        {
            var existing = await sessions.FindByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await runtime.MarkSessionAbnormalAsync(existing.MonthKey, existing.SessionId, DateTime.Now, cancellationToken)
                    .ConfigureAwait(false);
                await sessions.RemoveByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
            }

            serialNo = await serials.NextAsync(triggerTime, cancellationToken).ConfigureAwait(false);
            monthKey = PersistenceMonth(triggerTime);
            upsertSession = new PalletSession
            {
                SerialNo = serialNo,
                PalletCode = palletCode,
                StartTime = triggerTime,
                Status = SessionStatus.Open
            };
            active = new ActiveSessionIndex
            {
                PalletCode = palletCode,
                SerialNo = serialNo,
                MonthKey = monthKey,
                StartTime = triggerTime
            };
        }
        else
        {
            var existing = await sessions.FindByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                processAbnormal = true;
                processError = "未找到在制托盘会话（可能跳站）";
                serialNo = $"ORPHAN-{triggerTime:yyyyMMddHHmmss}";
                monthKey = PersistenceMonth(triggerTime);
                upsertSession = new PalletSession
                {
                    SerialNo = serialNo,
                    PalletCode = palletCode,
                    StartTime = triggerTime,
                    Status = SessionStatus.Abnormal
                };
                active = new ActiveSessionIndex
                {
                    PalletCode = palletCode,
                    SerialNo = serialNo,
                    MonthKey = monthKey,
                    StartTime = triggerTime
                };
            }
            else
            {
                serialNo = existing.SerialNo;
                monthKey = existing.MonthKey;
                existingSessionId = existing.SessionId;
                var previous = config.Stations
                    .Where(s => s.Enabled && s.Sequence < station.Sequence)
                    .Select(s => s.Id)
                    .ToList();
                if (previous.Count > 0)
                {
                    var history = await runtime.GetSessionRecordsAsync(existing.MonthKey, existing.SessionId, cancellationToken)
                        .ConfigureAwait(false);
                    var visited = history.Select(h => h.StationId).ToHashSet();
                    if (previous.Any(id => !visited.Contains(id)))
                    {
                        processAbnormal = true;
                        processError = "检测到跳站";
                    }
                }
            }
        }

        if (station.IsLastStation)
        {
            close = true;
            removeActive = true;
        }

        var recordJudgement = LimitEvaluator.Combine(products.Where(p => p.Occupied).Select(p => p.Judgement));
        short result = ResultCodes.Success;
        if (validationError)
        {
            result = ResultCodes.DataValidationFailed;
            recordJudgement = Judgement.Ng;
        }
        else if (processAbnormal)
        {
            result = ResultCodes.ProcessAbnormal;
            recordJudgement = Judgement.Ng;
        }

        var record = new CollectRecord
        {
            PalletSessionId = existingSessionId,
            SerialNo = serialNo,
            PalletCode = palletCode,
            StationId = station.Id,
            StationCode = station.Code,
            TriggerTime = triggerTime,
            CompleteTime = DateTime.Now,
            ResultCode = result,
            Judgement = recordJudgement,
            ErrorMessage = processError,
            RecipeCode = RecipeCode(config),
            Products = products,
            TagValues = tagValues
        };

        var save = new CollectSaveRequest
        {
            MonthKey = monthKey,
            UpsertSession = upsertSession,
            CloseSession = close,
            Record = record,
            Curves = curveWrites,
            ActiveSession = active,
            RemoveActiveSession = removeActive
        };

        try
        {
            await runtime.SaveAsync(save, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工站 {Station} 写库失败，转入本地缓存", station.Code);
            var spool = scope.ServiceProvider.GetRequiredService<ISpoolStore>();
            await spool.SaveAsync(save, cancellationToken).ConfigureAwait(false);
            record.ResultCode = ResultCodes.DatabaseWriteFailed;
            record.ErrorMessage = ex.Message;
            return Live(station, record, curveWrites, monthKey);
        }

        if (station.IsLastStation && config.Settings.MesEnabled && result == ResultCodes.Success)
        {
            var payload = JsonSerializer.Serialize(new
            {
                serialNo,
                palletCode,
                station = station.Code,
                judgement = recordJudgement.ToString(),
                time = record.CompleteTime
            });
            await mes.EnqueueSessionAsync(monthKey, record.PalletSessionId, serialNo, palletCode, payload, cancellationToken)
                .ConfigureAwait(false);
        }

        return Live(station, record, curveWrites, monthKey);
    }

    private async Task WriteResultAsync(
        Station station,
        PlcConnection connection,
        PlcRequestQueue queue,
        short resultCode,
        SystemSettings settings,
        CancellationToken cancellationToken)
    {
        if (!queue.Driver.TryParseAddress(station.TriggerAddress, out var address))
        {
            _logger.LogError("工站 {Station} 触发地址无法解析: {Address}", station.Code, station.TriggerAddress);
            return;
        }

        var retries = Math.Max(1, settings.WriteRetryCount);
        Exception? last = null;
        for (var i = 0; i < retries; i++)
        {
            try
            {
                await queue.WriteWordsAsync(address, ValueCodec.EncodeInt16(resultCode), cancellationToken)
                    .ConfigureAwait(false);
                var readBack = await queue.ReadWordsAsync(address, 1, cancellationToken).ConfigureAwait(false);
                if (readBack.Length > 0 && (short)readBack[0] == resultCode)
                {
                    return;
                }

                last = new PlcDriverException("写回校验不一致");
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(settings.WriteRetryDelayMs, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogError(last, "工站 {Station} 响应码写回失败", station.Code);
        SetStatus(station, StationRuntimeState.Fault, null, null, resultCode, Judgement.None, "响应码写回失败", null);
    }

    private void SetStatus(
        Station station,
        StationRuntimeState state,
        string? pallet,
        string? serial,
        short? code,
        Judgement judgement,
        string? error,
        int? durationMs,
        IReadOnlyList<StationLiveTag>? tags = null,
        IReadOnlyList<StationLiveCurve>? curves = null,
        string? monthKey = null,
        long? recordId = null)
    {
        var previous = _status.Stations.FirstOrDefault(x => x.StationId == station.Id);
        var completed = code is not null;
        _status.UpsertStation(new StationRuntimeStatus
        {
            StationId = station.Id,
            StationCode = station.Code,
            StationName = station.Name,
            Sequence = station.Sequence,
            State = state,
            LastPalletCode = pallet ?? previous?.LastPalletCode,
            LastSerialNo = serial ?? previous?.LastSerialNo,
            LastResultCode = code ?? previous?.LastResultCode,
            LastJudgement = judgement != Judgement.None ? judgement : previous?.LastJudgement ?? Judgement.None,
            LastCompleteTime = completed ? DateTime.Now : previous?.LastCompleteTime,
            LastDurationMs = durationMs ?? previous?.LastDurationMs,
            LastError = error,
            LastMonthKey = monthKey ?? previous?.LastMonthKey,
            LastRecordId = recordId is > 0 ? recordId : previous?.LastRecordId,
            LastTags = tags ?? previous?.LastTags ?? [],
            LastCurves = curves ?? previous?.LastCurves ?? []
        });
    }

    private static CollectOutcome Live(
        Station station,
        CollectRecord record,
        IReadOnlyList<CurvePayloadWrite>? curves = null,
        string? monthKey = null)
        => new()
        {
            Record = record,
            Tags = MapTags(station, record.TagValues),
            Curves = MapCurves(curves),
            MonthKey = monthKey ?? PersistenceMonth(record.TriggerTime)
        };

    private static IReadOnlyList<StationLiveTag> MapTags(Station station, IEnumerable<TagValue>? values)
    {
        if (values is null)
        {
            return [];
        }

        var units = station.Tags.ToDictionary(t => t.Id, t => t.Unit);
        return values.Select(v => new StationLiveTag
        {
            Name = string.IsNullOrWhiteSpace(v.TagName) ? v.TagCode : v.TagName,
            Display = v.TextValue ?? v.NumericValue?.ToString("0.###") ?? "-",
            Unit = units.GetValueOrDefault(v.TagId),
            OutOfLimit = v.IsOutOfLimit,
            Warning = v.IsWarning
        }).ToList();
    }

    private static IReadOnlyList<StationLiveCurve> MapCurves(IReadOnlyList<CurvePayloadWrite>? writes)
    {
        if (writes is null || writes.Count == 0)
        {
            return [];
        }

        return writes.Select(write =>
        {
            var y = write.Payload.Series.FirstOrDefault(s => s.Role == SeriesRole.Y)
                    ?? write.Payload.Series.FirstOrDefault();
            return new StationLiveCurve
            {
                Name = write.Record.CurveName,
                Values = y?.Values.ToArray() ?? []
            };
        }).ToList();
    }

    private sealed class CollectOutcome
    {
        public required CollectRecord Record { get; init; }
        public IReadOnlyList<StationLiveTag> Tags { get; init; } = [];
        public IReadOnlyList<StationLiveCurve> Curves { get; init; } = [];
        public string MonthKey { get; init; } = "";
    }

    private static string PersistenceMonth(DateTime time) => time.ToString("yyyyMM");

    /// <summary>本次判定所用的型号编码；未选择型号时为空串。</summary>
    private static string RecipeCode(AppConfigurationSnapshot config) => config.ActiveRecipe?.Code ?? "";

    /// <summary>
    /// 影子模式：拿缓存里的基线给刚算出的波形特征打分，只写入偏离字段。
    /// </summary>
    /// <remarks>
    /// <b>硬约束</b>：这里产生的一切结果都<b>不得</b>影响 <c>ResultCodes</c>、<c>Judgement</c>
    /// 或 <c>NgReason</c>。主判定链必须是确定性的、可审计的规则；而偏离分是统计推断，
    /// 样本少、基线漂移、型号切换都可能让它误报，绝不能回写 PLC 的握手码。
    /// 它现在的作用是"只记录"：攒够对比数据之后再决定要不要升级成预警。
    /// <para>
    /// 三种情况一律不打分（留 null）：缓存尚未建立、缓存型号与当前型号不一致、该序列没有基线。
    /// 宁可没有数字，也不给一个来源不明的分数。
    /// </para>
    /// </remarks>
    private void ApplyBaselineDeviation(CurveFeature row, int curveDefinitionId, AppConfigurationSnapshot config)
    {
        var baseline = _baselines.Current;
        if (baseline is null)
        {
            return;
        }

        // 缓存里的基线属于某个型号：当前判定用的型号变了就必须整批失效，
        // 不能拿旧型号的波形分布去评判新型号的波形。
        if (!string.Equals(baseline.RecipeCode, RecipeCode(config), StringComparison.Ordinal))
        {
            return;
        }

        var template = baseline.Find(curveDefinitionId, row.SeriesName);
        if (template is null)
        {
            return;
        }

        var score = CurveTemplateMatcher.Score(template, row);
        row.DeviationRmsZ = score.RmsZ;
        row.DeviationVerdict = score.Verdict;
        row.DeviationWorstDimension = score.WorstDimension;
        row.BaselineSampleCount = template.SampleCount;
    }
}
