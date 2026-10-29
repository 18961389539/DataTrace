using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataTrace.Infrastructure.Seeding;

partial class DatabaseSeeder
{
    /// <summary>
    /// 给演示工站补一批自定义设备报警（工站配置「报警」Tab）。
    /// 幂等：同站同名已存在则跳过；关联现有点位并启用，便于看板/报警页看到限值触发。
    /// </summary>
    private async Task EnsureDemoStationAlarmsAsync(CancellationToken cancellationToken)
    {
        var stations = await _db.Stations
            .Include(s => s.Tags)
            .Include(s => s.Alarms)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var added = 0;
        foreach (var station in stations.OrderBy(s => s.Sequence).ThenBy(s => s.Code, StringComparer.Ordinal))
        {
            // CreateStation 风格：压力 + 工站温度
            added += TryAddDemoAlarm(
                station,
                name: "压力过高",
                tagName: "压力",
                compare: StationAlarmCompare.Above,
                limit: 16,
                remark: "演示：超过压力黄线预警上限（16 kN）");
            added += TryAddDemoAlarm(
                station,
                name: "压力过低",
                tagName: "压力",
                compare: StationAlarmCompare.Below,
                limit: 6,
                remark: "演示：低于压力黄线预警下限（6 kN）");
            added += TryAddDemoAlarm(
                station,
                name: "温度过高",
                tagName: "工站温度",
                compare: StationAlarmCompare.Above,
                limit: 60,
                remark: "演示：超过工站温度黄线（60℃）");

            // 压装站：压装1 压力/位移
            added += TryAddDemoAlarm(
                station,
                name: "压装1压力过高",
                tagName: "压装1压力值",
                compare: StationAlarmCompare.Above,
                limit: 14,
                remark: "演示：压装通道1压力超限");
            added += TryAddDemoAlarm(
                station,
                name: "压装1位移过低",
                tagName: "压装1位移值",
                compare: StationAlarmCompare.Below,
                limit: 9,
                remark: "演示：压装通道1位移不足");

            // 检测站（仅有离散结果点位）：挂第一条带「结果」的点位
            if (!station.Tags.Any(t =>
                    string.Equals(t.Name, "压力", StringComparison.Ordinal)
                    || string.Equals(t.Name, "工站温度", StringComparison.Ordinal)
                    || string.Equals(t.Name, "压装1压力值", StringComparison.Ordinal)))
            {
                var resultTag = station.Tags
                    .Where(t => t.Name.Contains("结果", StringComparison.Ordinal))
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (resultTag is not null)
                {
                    added += TryAddDemoAlarm(
                        station,
                        name: "检测通道异常",
                        tagName: resultTag.Name,
                        compare: StationAlarmCompare.Above,
                        limit: 14,
                        remark: $"演示：关联点位「{resultTag.Name}」，模拟超限码");
                }
            }
        }

        if (added == 0)
        {
            return;
        }

        var version = await _db.ConfigVersions.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (version is not null)
        {
            version.Version++;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("已为演示工站补充 {Count} 条自定义设备报警", added);
    }

    private static int TryAddDemoAlarm(
        Station station,
        string name,
        string tagName,
        StationAlarmCompare compare,
        double limit,
        string remark)
    {
        if (station.Alarms.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        var tag = station.Tags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.Ordinal));
        if (tag is null || tag.Id <= 0)
        {
            return 0;
        }

        station.Alarms.Add(new StationAlarmDefinition
        {
            StationId = station.Id,
            Name = name,
            Enabled = true,
            TagDefinitionId = tag.Id,
            Compare = compare,
            Limit = limit,
            SampleIntervalMs = 2000,
            Remark = remark
        });
        return 1;
    }
}
