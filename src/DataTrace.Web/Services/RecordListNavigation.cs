using System.Globalization;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Shared;
using Microsoft.AspNetCore.WebUtilities;

namespace DataTrace.Web.Services;

public static class RecordListNavigation
{
    public static readonly (string Key, CollectSortField Field)[] SortKeys =
    [
        ("time", CollectSortField.TriggerTime), ("serial", CollectSortField.SerialNo),
        ("pallet", CollectSortField.PalletCode), ("station", CollectSortField.StationCode),
        ("recipe", CollectSortField.RecipeCode), ("judge", CollectSortField.Judgement),
        ("result", CollectSortField.ResultCode), ("duration", CollectSortField.DurationMs)
    ];

    public static bool IsListUrl(string? url)
        => url is not null && url.Split('?')[0] == "query" && !url.Contains('#');

    public static CollectQueryRequest ReadRequest(string url, int skip, int take)
    {
        var query = QueryHelpers.ParseQuery(url.Contains('?') ? url[url.IndexOf('?')..] : "");
        string? Value(string name) => query.TryGetValue(name, out var value) ? value.ToString() : null;
        var from = DateTime.TryParse(Value("from"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? start : DateTime.Today;
        var to = DateTime.TryParse(Value("to"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            ? end : DateTime.Today;
        var recipe = Value("norecipe") == "1" ? "" : Value("recipe")?.Trim();
        var aliases = query.TryGetValue("alias", out var values)
            ? values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToArray() : [];
        var recipes = !string.IsNullOrEmpty(recipe) && aliases.Length > 0
            ? new[] { recipe }.Concat(aliases).Distinct(StringComparer.Ordinal).ToArray() : null;
        return new CollectQueryRequest
        {
            From = from, To = DateRangeFilter.QueryEnd(to),
            PalletCode = Value("pallet")?.Trim(), SerialNo = Value("serial")?.Trim(),
            StationId = int.TryParse(Value("station"), out var station) && station > 0 ? station : null,
            Judgement = Enum.TryParse<Judgement>(Value("judge"), true, out var judge) ? judge : null,
            ResultCode = short.TryParse(Value("result"), out var result) && result > 0 ? result : null,
            RecipeCode = recipes is null ? recipe : null, RecipeCodes = recipes,
            SortBy = ParseSortField(Value("sort")), SortDescending = Value("dir") != "asc",
            Skip = skip, Take = take
        };
    }

    public static CollectSortField ParseSortField(string? key)
        => SortKeys.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Field;

    public static string PageUrl(string url, int position)
    {
        var query = QueryHelpers.ParseQuery(url.Contains('?') ? url[url.IndexOf('?')..] : "");
        var size = int.TryParse(query.GetValueOrDefault("size").ToString(), out var parsed) && parsed is 20 or 50 or 100 ? parsed : 20;
        var page = position / size + 1;
        if (page == 1) query.Remove("page");
        else query["page"] = page.ToString(CultureInfo.InvariantCulture);
        return QueryHelpers.AddQueryString("query", query);
    }

    public static string DetailUrl(CollectRecordListItem item, string backUrl, int position)
        => QueryHelpers.AddQueryString($"/query/{Uri.EscapeDataString(item.MonthKey)}/{item.Record.Id}",
            new Dictionary<string, string?>
            {
                ["back"] = PageUrl(backUrl, position),
                ["position"] = position.ToString(CultureInfo.InvariantCulture)
            });
}