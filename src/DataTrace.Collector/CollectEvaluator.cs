using DataTrace.Application.Evaluation;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Planning;

namespace DataTrace.Collector;

public sealed class CollectAssessment
{
    public required List<TagValue> TagValues { get; init; }
    public required List<ProductRecord> Products { get; init; }
    public required List<CurvePayloadWrite> Curves { get; init; }
    public bool ValidationError { get; init; }
    public bool QualityRejected { get; init; }
    public string? ValidationMessage { get; init; }
}

/// <summary>把已经读到的字和文件值判成点位、产品位和曲线特征。不做 IO。</summary>
public static class CollectEvaluator
{
    public static CollectAssessment Evaluate(
        Station station,
        PlcConnection connection,
        ReadPlan plan,
        ushort[][] buffers,
        FileSourceRead? source,
        ICurveBaselineCache baselines,
        Recipe? recipe)
    {
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
        var qualityRejected = false;
        string? validationMessage = null;

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
                var fromFile = tag.Source == TagDataSource.JsonFile;
                var words = fromFile ? [] : plan.GetWords($"tag_{tag.Id}", buffers);
                double? numeric = null;
                string? text = null;
                if (fromFile)
                {
                    var read = source!.Values.GetValueOrDefault(tag.Id);
                    text = read?.Text;
                    numeric = read?.Numeric;
                }
                else if (tag.DataType == PlcDataType.String)
                {
                    text = ValueCodec.DecodeAscii(words, tag.Length, connection.StringHighByteFirst);
                }
                else
                {
                    numeric = ValueCodec.DecodeNumeric(words, tag.DataType, connection.FloatWordOrder, tag.Scale, tag.Offset);
                    if (tag.IsRequired && words.Length == 0)
                    {
                        validationError = true;
                    }
                }

                if (tag.DataType == PlcDataType.String && tag.IsRequired && string.IsNullOrWhiteSpace(text))
                {
                    validationError = true;
                }

                var limits = RecipeLimitResolver.Resolve(tag, recipe);
                var status = tag.DataType == PlcDataType.String
                    ? LimitStatus.None
                    : LimitEvaluator.Evaluate(limits, numeric, tag.IsRequired);
                var outOfLimit = status == LimitStatus.OutOfSpec;
                if (outOfLimit)
                {
                    // 没读到数是采集失败；读到了、但越过规格，才是质量不合格。
                    if (numeric is null)
                    {
                        validationError = true;
                    }
                    else
                    {
                        qualityRejected = true;
                    }

                    posJudgements.Add(Judgement.Ng);
                    ngReason ??= $"{tag.Name}超限";
                }
                else if (limits.HasAny)
                {
                    posJudgements.Add(Judgement.Ok);
                }

                tagValues.Add(new TagValue
                {
                    TagId = tag.Id,
                    TagName = tag.Name,
                    PositionIndex = pos,
                    DataType = tag.DataType,
                    NumericValue = numeric,
                    TextValue = text,
                    IsOutOfLimit = outOfLimit,
                    IsWarning = status == LimitStatus.Warning,
                    LowerLimit = limits.Lower,
                    UpperLimit = limits.Upper,
                    WarningLowerLimit = limits.WarningLower,
                    WarningUpperLimit = limits.WarningUpper
                });
            }

            foreach (var curve in station.Curves.Where(c => c.Enabled && c.PointCount > 0 && c.PositionIndex == pos))
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

                    var features = CurveFeatureExtractor.Extract(values);
                    if (!features.IsValid)
                    {
                        validationError = true;
                        validationMessage ??= $"{curve.Name}波形序列 {series.Name} 包含 NaN 或 Infinity；已保留原始曲线，跳过特征与曲线判据";
                    }
                    else
                    {
                        var featureRow = CurveFeatureExtractor.ToEntity(series.Name, series.Role, features);
                        ApplyBaselineDeviation(featureRow, curve.Id, recipe, baselines);
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

                            qualityRejected = true;
                            posJudgements.Add(Judgement.Ng);
                            ngReason ??= $"{curve.Name}波形异常：{reasons[0]}";
                        }
                    }

                    seriesPayloads.Add(new CurveSeriesPayload
                    {
                        Name = series.Name,
                        Role = series.Role,
                        Values = values
                    });
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

        return new CollectAssessment
        {
            TagValues = tagValues,
            Products = products,
            Curves = curveWrites,
            ValidationError = validationError,
            QualityRejected = qualityRejected,
            ValidationMessage = validationMessage
        };
    }

    private static void ApplyBaselineDeviation(
        CurveFeature row,
        int curveDefinitionId,
        Recipe? recipe,
        ICurveBaselineCache baselines)
    {
        var baseline = baselines.Current;
        if (baseline is null || !baseline.IsFresh(DateTime.Now))
        {
            return;
        }

        var recipeCode = recipe?.Code ?? "";
        if (!string.Equals(baseline.RecipeCode, recipeCode, StringComparison.Ordinal))
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
