using System.ComponentModel;
using System.Text.Json;
using DataTrace.Application.Alarms;
using DataTrace.Application.Configuration;
using DataTrace.Mcp.Models;
using DataTrace.Mcp.Tools;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Resources;

[McpServerResourceType]
public sealed class CatalogResources(
    IConfigRepository config,
    IAlarmIncidents alarms,
    McpCallContext call,
    ToolAudit audit)
{
    [McpServerResource(UriTemplate = "datatrace://stations", Name = "stations",
        Title = "工站与点位目录", MimeType = "application/json")]
    [Description("工站、点位、单位、用途与规格/预警限值的稳定目录。需要 read scope。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<string> Stations(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var snapshot = await config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var entries = snapshot.Stations
            .OrderBy(s => s.Sequence)
            .Select(s => new StationCatalogEntryDto(
                new StationDto(s.Id, s.Code, s.Name, s.Sequence, s.Enabled,
                    s.IsFirstStation, s.IsLastStation, s.Tags.Count, s.Curves.Count),
                s.Tags.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new TagDto(
                    t.Id, t.StationId, t.Name, t.Unit, t.Purpose.ToString(),
                    t.LowerLimit, t.UpperLimit, t.WarningLowerLimit, t.WarningUpperLimit,
                    t.TargetValue, t.IsRequired, t.Enabled, t.Source.ToString())).ToList()))
            .ToList();

        await audit.RecordAsync("resource.stations", "Success", entries.Count,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(entries, McpJson.Options);
    }

    [McpServerResource(UriTemplate = "datatrace://recipes", Name = "recipes",
        Title = "产品型号与限值", MimeType = "application/json")]
    [Description("产品型号及其点位限值覆盖。需要 read scope。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<string> Recipes(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var snapshot = await config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var entries = snapshot.Recipes.Select(r => new RecipeCatalogEntryDto(
            new RecipeDto(r.Id, r.Code, r.Name, r.Model, r.Enabled, r.Limits.Count),
            r.Limits.Select(l => new RecipeLimitDto(
                l.TagId, l.LowerLimit, l.UpperLimit, l.WarningLowerLimit,
                l.WarningUpperLimit, l.TargetValue)).ToList())).ToList();

        await audit.RecordAsync("resource.recipes", "Success", entries.Count,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(entries, McpJson.Options);
    }

    [McpServerResource(UriTemplate = "datatrace://alarms/open", Name = "open-alarms",
        Title = "当前报警", MimeType = "application/json")]
    [Description("待关注报警与最近关闭报警。需要 read scope。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<string> OpenAlarms(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var attention = await alarms.ListAttentionAsync(cancellationToken).ConfigureAwait(false);
        var closed = await alarms.ListRecentClosedAsync(20, cancellationToken).ConfigureAwait(false);
        var entries = attention.Concat(closed).Select(AlarmTools.ToDto).ToList();
        await audit.RecordAsync("resource.open_alarms", "Success", entries.Count,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(entries, McpJson.Options);
    }
}
