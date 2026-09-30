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
    public Task<IReadOnlyList<Station>> GetStationsAsync(CancellationToken cancellationToken = default)
        => ReadListAsync(
            db => db.Stations.AsNoTracking()
                .Include(x => x.PlcConnection)
                .Include(x => x.Positions)
                .Include(x => x.Tags)
                .Include(x => x.Curves).ThenInclude(c => c.Series)
                .Include(x => x.Curves).ThenInclude(c => c.Criteria)
                .OrderBy(x => x.Sequence)
                .ToListAsync(cancellationToken),
            cancellationToken);

    /// <summary>
    /// 单个工站的详情读：返回未被跟踪的图，编辑表单可以改它，保存时再收成命令。
    /// </summary>
    public Task<Station?> GetStationAsync(int id, CancellationToken cancellationToken = default)
        => _db.Stations.AsNoTracking()
            .Include(x => x.Positions)
            .Include(x => x.Tags)
            .Include(x => x.Curves).ThenInclude(c => c.Series)
            .Include(x => x.Curves).ThenInclude(c => c.Criteria)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<int> SaveStationAsync(SaveStationCommand command, CancellationToken cancellationToken = default)
    {
        var station = command.ToEntity();
        StationWriteNormalizer.Apply(station);
        await EnsureStationInvariantsAsync(station, cancellationToken).ConfigureAwait(false);

        if (station.Id == 0)
        {
            _db.Stations.Add(station);
        }
        else
        {
            var existing = await _db.Stations
                .Include(x => x.Positions)
                .FirstOrDefaultAsync(x => x.Id == station.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.Stations.Add(station);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(station);
                existing.Positions.Clear();
                foreach (var p in station.Positions)
                {
                    // 命令物化出来的是新对象，但带着原来的主键。清掉再插入，避免和刚 Clear 的行抢同一个键。
                    p.Id = 0;
                    p.StationId = existing.Id;
                    existing.Positions.Add(p);
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
        return station.Id;
    }

    /// <summary>
    /// 工站级的硬约束：编码唯一、握手参数可用、以及整条线的拓扑自洽。
    /// </summary>
    /// <remarks>
    /// 界面也做同样的校验，但那只是 UI：这些规则一旦被绕过（历史脏配置、脚本直接写库），
    /// 后果都落在采集端，而且表现形式很难往"配置错了"上想 ——
    /// 触发值与回写码相同会让工站被无限重复触发；一条线没有末站则托盘会话永不关闭、MES 从不上报。
    /// </remarks>
    private async Task EnsureStationInvariantsAsync(Station station, CancellationToken cancellationToken)
    {
        if (station.Code.Length == 0)
        {
            throw new InvalidOperationException("工站编码不能为空");
        }

        if (StationConfigLimits.TriggerValueError(station.TriggerValue) is { } triggerError)
        {
            throw new InvalidOperationException($"工站 {station.Code} 的{triggerError}");
        }

        if (StationConfigLimits.PalletCodeLengthError(station.PalletCodeLength) is { } lengthError)
        {
            throw new InvalidOperationException($"工站 {station.Code} 的{lengthError}");
        }

        // 文件源点位必须有一个可读的文件：采集侧遇到"有文件源点位但路径为空"每次都失败，
        // 与其让现场对着结果码 9 排查，不如在保存工站这一步就说清楚。
        // 只校验"有文件源点位时非空"，不校验存在性：文件由设备写，配置时它完全可以还没出现。
        if (station.DataFilePath.Length == 0)
        {
            var fileSourceTag = await _db.Tags.AsNoTracking()
                .Where(x => x.StationId == station.Id && x.Source == TagDataSource.JsonFile)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (fileSourceTag is not null)
            {
                throw new InvalidOperationException(
                    $"工站 {station.Code} 的点位 {fileSourceTag} 取自数据文件，但「数据文件路径」为空");
            }
        }

        if (await _db.Stations.AnyAsync(
                x => x.Id != station.Id && x.Code.ToLower() == station.Code.ToLower(),
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"工站编码「{station.Code}」已存在");
        }

        // 顺序与首末站只按"启用的工站"判定：停用的工站不参与采集，
        // 也就不该挡住维护期间的调整（例如临时停掉唯一的末站）。
        var others = await _db.Stations.AsNoTracking()
            .Where(x => x.Id != station.Id && x.Enabled)
            .Select(x => new { x.Code, x.Sequence, x.IsFirstStation, x.IsLastStation })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!station.Enabled)
        {
            return;
        }

        if (station.Sequence <= 0)
        {
            throw new InvalidOperationException($"工站 {station.Code} 的产线顺序必须大于 0");
        }

        if (others.FirstOrDefault(x => x.Sequence == station.Sequence) is { } sameOrder)
        {
            throw new InvalidOperationException($"产线顺序 {station.Sequence} 已被工站 {sameOrder.Code} 占用");
        }

        if (station.IsFirstStation && others.FirstOrDefault(x => x.IsFirstStation) is { } first)
        {
            throw new InvalidOperationException($"工站 {first.Code} 已是首站；一条线只能有一个启用的首站");
        }

        if (station.IsLastStation && others.FirstOrDefault(x => x.IsLastStation) is { } last)
        {
            throw new InvalidOperationException($"工站 {last.Code} 已是末站；一条线只能有一个启用的末站");
        }
    }

    public async Task DeleteStationAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.Stations.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        // 点位随工站级联删除，它们的型号覆盖行必须一起清掉（与 DeleteTagAsync 同一套做法）：
        // 留下的悬空行在限值矩阵里看不见，却会被"覆盖点位 N 个"继续算进去。
        var tagIds = await _db.Tags.AsNoTracking()
            .Where(x => x.StationId == id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (tagIds.Count > 0)
        {
            var limits = await _db.RecipeLimits
                .Where(x => tagIds.Contains(x.TagId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _db.RecipeLimits.RemoveRange(limits);
        }

        _db.Stations.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> SaveTagAsync(SaveTagCommand command, CancellationToken cancellationToken = default)
    {
        var tag = command.ToEntity();
        tag.Name = (tag.Name ?? "").Trim();
        if (tag.Name.Length == 0)
        {
            throw new InvalidOperationException("点位名称不能为空");
        }

        // 点位一律属于这一件产品。工站级（0）已取消：空位不读，超限记在这一件上。
        tag.PositionIndex = 1;

        // 库里 (StationId, Name) 上有唯一索引，重名会抛出 SQLite 的原始错误；
        // 先查出来，用户看到的才是"哪个点位重名"（与型号/PLC 同一套做法）。
        if (await _db.Tags.AnyAsync(
                x => x.StationId == tag.StationId && x.Id != tag.Id && x.Name.ToLower() == tag.Name.ToLower(),
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"点位名称「{tag.Name}」在该工站下已存在");
        }

        // 对话框里也校验这一条，但那只是 UI：从 MES 或其它入口直接写库照样能留下自相矛盾的限值，
        // 而采集端只会默默把黄线收敛进红线，明细页上显示的预警限就跟实际判据不是一回事了。
        if (TagLimits.From(tag).ConsistencyError() is { } error)
        {
            throw new InvalidOperationException($"点位 {tag.Name} 的限值互相矛盾：{error}");
        }

        // 文件源点位没有可读的路径：这类配置在采集侧表现为"每次触发都失败（结果码 9）"，
        // 与其让现场对着结果码排查，不如在保存点位这一步就说清楚。
        // 只校验非空，不校验存在性：文件由设备写，配置时它完全可以还没出现。
        if (tag.Source == TagDataSource.JsonFile)
        {
            var station = await _db.Stations.AsNoTracking()
                .Where(x => x.Id == tag.StationId)
                .Select(x => new { x.Code, x.DataFilePath })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (station is null)
            {
                throw new InvalidOperationException($"点位 {tag.Name} 所属的工站不存在，无法保存");
            }

            if (string.IsNullOrWhiteSpace(station.DataFilePath))
            {
                throw new InvalidOperationException(
                    $"点位 {tag.Name} 取自数据文件，但工站 {station.Code} 还没配「数据文件路径」，请先到「点位」页签顶部填写或选择");
            }
        }

        if (tag.Id == 0)
        {
            _db.Tags.Add(tag);
        }
        else
        {
            // 不要 Update(detached)：同作用域里若已有同 Id 跟踪实例会触发 EF 冲突。
            // 与 SaveCurveAsync 一致：加载已跟踪实体再 SetValues。
            var existing = await _db.Tags.FirstOrDefaultAsync(x => x.Id == tag.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                _db.Tags.Add(tag);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(tag);
            }

            // Bool/String 不能参与数值限值覆盖：改类型时清掉遗留 RecipeLimit
            if (tag.DataType is PlcDataType.Bool or PlcDataType.String)
            {
                await RemoveRecipeLimitsForTagAsync(tag.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
        return tag.Id;
    }

    public async Task DeleteTagAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.Tags.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        // 同一事务清掉型号覆盖行，避免 TagId 悬空孤儿。
        await RemoveRecipeLimitsForTagAsync(id, cancellationToken).ConfigureAwait(false);
        _db.Tags.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }
}
