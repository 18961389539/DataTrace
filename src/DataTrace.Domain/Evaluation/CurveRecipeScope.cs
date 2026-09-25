using DataTrace.Domain.Entities;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 波形的"本型号"范围：当前生效型号的编码，加上它改编码前的历史编码。
/// </summary>
/// <remarks>
/// 改过编码的型号，历史样本仍写着旧码，若只认新码，切换/改名之后那段时间的波形
/// 会被当成"别的型号"整段排除 —— 基线空窗、页面显示"没有属于当前型号的样本"。
/// 采集端与波形基线页必须用同一套规则，否则两边会得出不一样的结论：
/// 记录上带着偏离分（采集端按含旧码建了基线），页面却说一条样本都没有。
/// </remarks>
public static class CurveRecipeScope
{
    /// <summary>
    /// 允许的编码集合：<paramref name="activeCode"/>（空串表示未选型号）+ 逗号分隔的
    /// <paramref name="previousCodes"/>，按顺序去重。
    /// </summary>
    public static IReadOnlyList<string> AllowedCodes(string? activeCode, string? previousCodes)
    {
        var codes = new List<string> { activeCode ?? "" };
        if (!string.IsNullOrWhiteSpace(previousCodes))
        {
            foreach (var part in previousCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!codes.Contains(part, StringComparer.Ordinal))
                {
                    codes.Add(part);
                }
            }
        }

        return codes;
    }

    /// <summary>
    /// 按编码反查型号：既认当前编码，也认历史编码（改码后记录里写的仍是旧码）。
    /// </summary>
    /// <remarks>
    /// 报表按型号筛选时，用户能选到的编码来自历史记录，其中就可能是旧码。
    /// 只按当前编码精确匹配，这些筛选会查不到型号，于是拿着点位默认限值去给那批样本算 Cp/Cpk，
    /// 报表上却仍标着"限值来自配置"—— 数字与样本不是一套口径。
    /// </remarks>
    public static Recipe? FindByAnyCode(IReadOnlyList<Recipe> recipes, string? code)
        => string.IsNullOrEmpty(code)
            ? null
            : recipes.FirstOrDefault(r => AllowedCodes(r.Code, r.PreviousCodes).Contains(code, StringComparer.Ordinal));

    /// <summary>
    /// 把历史编码归一到型号当前的编码；不属于任何型号（或本身就是当前码）时原样返回。
    /// </summary>
    /// <remarks>
    /// 按型号汇总报表时用：改过编码的型号在区间内会留下新旧两个编码的记录，
    /// 按字面分组会让同一个型号裂成两行（旧码那行没有名称，显示成"旧编码 xxx"），
    /// 而曲线基线是把旧码算作本型号的 —— 两处必须给同一个答案。
    /// </remarks>
    public static string CanonicalCode(string? code, IReadOnlyList<Recipe> recipes)
    {
        if (string.IsNullOrEmpty(code))
        {
            return code ?? "";
        }

        var owner = FindByAnyCode(recipes, code);
        return owner?.Code ?? code;
    }
}