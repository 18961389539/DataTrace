using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Evaluation;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

partial class ConfigRepository
{
    public async Task<int> SaveCurveAsync(SaveCurveCommand command, CancellationToken cancellationToken = default)
    {
        var curve = command.ToEntity();
        curve.Code = curve.Code.Trim();
        if (curve.Code.Length == 0)
        {
            throw new InvalidOperationException("曲线编码不能为空");
        }

        if (StationConfigLimits.PointCountError(curve.PointCount) is { } pointError)
        {
            throw new InvalidOperationException($"曲线 {curve.Code} 的{pointError}");
        }

        curve.PositionIndex = 1;

        // (StationId, Code) 上事实上唯一：PositionIndex 恒为 1，按主键之外的重复编码先给出中文提示。
        if (await _db.Curves.AnyAsync(
                x => x.StationId == curve.StationId && x.Id != curve.Id && x.Code.ToLower() == curve.Code.ToLower(),
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"曲线编码「{curve.Code}」在该工站下已存在");
        }

        if (curve.Id == 0)
        {
            _db.Curves.Add(curve);
        }
        else
        {
            var existing = await _db.Curves
                .Include(x => x.Series)
                .FirstOrDefaultAsync(x => x.Id == curve.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.Curves.Add(curve);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(curve);
                existing.Series.Clear();
                foreach (var s in curve.Series)
                {
                    s.Id = 0;
                    s.CurveDefinitionId = existing.Id;
                    existing.Series.Add(s);
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
        return curve.Id;
    }

    public async Task SaveCurveCriteriaAsync(int curveId, IReadOnlyList<SaveCurveCriterionCommand> criteria, CancellationToken cancellationToken = default)
    {
        if (!await _db.Curves.AnyAsync(x => x.Id == curveId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await SyncCriteriaAsync(curveId, criteria.Select(x => x.ToEntity()), cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 判据按主键做增量同步：传入的集合即最终状态。
    /// 判定增删改一律以**数据库里的行**为准，不读也不写调用方的导航集合 ——
    /// 调用方手上的往往是同一个被跟踪实例，替换它的集合会和 EF 的导航修正互相打架：
    /// Clear + 重新 Add 会让同一主键在一次 SaveChanges 里既删除又插入，
    /// 而把克隆实体塞进导航集合则会让同一行在集合里留下两份（一份是永不落库的幽灵）。
    /// </summary>
    private async Task SyncCriteriaAsync(int curveId, IEnumerable<CurveCriterion> incoming, CancellationToken cancellationToken)
    {
        var targets = incoming.ToList();
        var stored = await _db.CurveCriteria
            .Where(x => x.CurveDefinitionId == curveId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = stored.ToDictionary(x => x.Id);
        var keep = new HashSet<int>();

        foreach (var item in targets)
        {
            if (item.Id != 0 && byId.TryGetValue(item.Id, out var current))
            {
                CopyCriterion(item, current);
                keep.Add(current.Id);
            }
            else
            {
                // 新增行一律新建实体交给 EF 挂载：传进来的可能是编辑用的克隆体，
                // 直接复用会让克隆体进入导航集合而真正的跟踪实体另有一份。
                var fresh = new CurveCriterion { CurveDefinitionId = curveId };
                CopyCriterion(item, fresh);
                _db.CurveCriteria.Add(fresh);
            }
        }

        foreach (var stale in stored.Where(x => !keep.Contains(x.Id)).ToList())
        {
            _db.CurveCriteria.Remove(stale);
        }
    }

    private static void CopyCriterion(CurveCriterion from, CurveCriterion to)
    {
        to.SeriesName = from.SeriesName;
        to.Enabled = from.Enabled;
        to.PeakMin = from.PeakMin;
        to.PeakMax = from.PeakMax;
        to.MeanMin = from.MeanMin;
        to.MeanMax = from.MeanMax;
        to.AreaMin = from.AreaMin;
        to.AreaMax = from.AreaMax;
        to.RiseSlopeMin = from.RiseSlopeMin;
        to.RiseSlopeMax = from.RiseSlopeMax;
        to.HoldSlopeMin = from.HoldSlopeMin;
        to.HoldSlopeMax = from.HoldSlopeMax;
        to.FallRatioMax = from.FallRatioMax;
        to.MaxStepMax = from.MaxStepMax;
        to.StdDevMax = from.StdDevMax;
        to.OscillationMax = from.OscillationMax;
    }

    public async Task DeleteCurveAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.Curves.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        _db.Curves.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }
}
