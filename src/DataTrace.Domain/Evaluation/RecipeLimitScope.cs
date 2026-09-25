using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 哪些点位可以配「型号限值覆盖」。
/// </summary>
/// <remarks>
/// 这份规则必须只有一处：型号限值对话框按它列出点位，启动时的历史脏数据清理按它判断哪些覆盖行还有效。
/// 曾经两处各写一份（一边写"不是 String/Bool"，一边写枚举白名单），改了一边就会把另一边判成脏数据。
/// <para>
/// 刻意<b>不</b>按 <see cref="TagDefinition.Enabled"/> 过滤：停用点位的历史覆盖值要留在限值矩阵里
/// 看得见、改得动。按"启用中"过滤的后果是"打开对话框什么都没改，一保存就把那行删了"。
/// </para>
/// </remarks>
public static class RecipeLimitScope
{
    /// <summary>能参与数值限值判定的数据类型（枚举白名单，可直接用于 SQL 的 IN 翻译）。</summary>
    public static readonly IReadOnlyList<PlcDataType> NumericTypes =
    [
        PlcDataType.Int16,
        PlcDataType.Int32,
        PlcDataType.Float,
        PlcDataType.Double
    ];

    /// <summary>该数据类型能否参与数值限值判定。</summary>
    public static bool IsNumeric(PlcDataType type) => NumericTypes.Contains(type);

    /// <summary>该点位是否可以有型号覆盖行。</summary>
    public static bool CanOverride(TagDefinition tag) => IsNumeric(tag.DataType);

    /// <summary>这些工站下全部可覆盖的点位（含已停用的）。</summary>
    public static IEnumerable<TagDefinition> OverridableTags(IEnumerable<Station> stations)
        => stations.SelectMany(s => s.Tags).Where(CanOverride);
}
