using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;

namespace DataTrace.Collector;

/// <summary>
/// 首站记下当时的型号，这件后面的工站都用这一套。
/// 中途换成新型号，只作用于下一件。在制索引里没有型号时，沿用当前型号。
/// </summary>
public static class SessionRecipe
{
    public static Recipe? Select(AppConfigurationSnapshot config, bool firstStation, string? frozenCode)
    {
        if (firstStation || string.IsNullOrWhiteSpace(frozenCode))
        {
            return config.ActiveRecipe;
        }

        return config.Recipes.FirstOrDefault(recipe =>
            string.Equals(recipe.Code, frozenCode.Trim(), StringComparison.Ordinal));
    }

    public static string RecordCode(bool firstStation, string? frozenCode, Recipe? active)
    {
        if (!firstStation && !string.IsNullOrWhiteSpace(frozenCode))
        {
            return frozenCode.Trim();
        }

        return active?.Code ?? "";
    }
}
