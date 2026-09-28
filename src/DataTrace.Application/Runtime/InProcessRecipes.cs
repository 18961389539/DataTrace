using DataTrace.Domain.Entities;

namespace DataTrace.Application.Runtime;

/// <summary>还在线上的件，按进首站时记下的型号归成一堆。</summary>
public readonly record struct InProcessRecipe(string Code, int Count);

public static class InProcessRecipes
{
    public static IReadOnlyList<InProcessRecipe> From(IEnumerable<ActiveSessionIndex> sessions)
        => sessions
            .GroupBy(session => session.RecipeCode ?? "", StringComparer.Ordinal)
            .Select(group => new InProcessRecipe(group.Key, group.Count()))
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 在制件的型号和下一件将要用的型号不一致时，返回看板上那一句。
    /// 全都一致、或线上没有件时返回 null。
    /// </summary>
    public static string? Note(string? activeCode, IReadOnlyList<InProcessRecipe> rows)
    {
        var active = activeCode ?? "";
        var different = rows
            .Where(item => item.Count > 0 && !string.Equals(item.Code ?? "", active, StringComparison.Ordinal))
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ToList();
        if (different.Count == 0)
        {
            return null;
        }

        var pieces = string.Join("，", different.Select(item => $"{item.Count} 件仍用{Display(item.Code)}"));
        return $"在制 {pieces}。下一件起用{Display(active)}。";
    }

    public static string Display(string? code)
        => string.IsNullOrWhiteSpace(code) ? "点位默认限值" : code.Trim();
}
