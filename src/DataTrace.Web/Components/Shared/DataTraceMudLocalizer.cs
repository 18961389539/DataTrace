using Microsoft.Extensions.Localization;
using MudBlazor;

namespace DataTrace.Web.Components.Shared;

/// <summary>为 MudBlazor 的通用控件提供中文可访问名称与日历导航文本。</summary>
public sealed class DataTraceMudLocalizer : MudLocalizer
{
    private static readonly IReadOnlyDictionary<string, string> Translations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Open Date Picker"] = "打开日期选择器",
            ["Close Picker"] = "关闭日期选择器",
            ["MudBaseDatePicker_NextMonth"] = "下个月：{0}",
            ["MudBaseDatePicker_NextYear"] = "下一年：{0}年",
            ["MudBaseDatePicker_PrevMonth"] = "上个月：{0}",
            ["MudBaseDatePicker_PrevYear"] = "上一年：{0}年",
            ["MudInput_Clear"] = "清除",
            ["MudInput_Decrement"] = "减少数值",
            ["MudInput_Increment"] = "增加数值",
            ["MudPagination_CurrentPage"] = "当前第 {0} 页",
            ["MudPagination_FirstPage"] = "第一页",
            ["MudPagination_LastPage"] = "最后一页",
            ["MudPagination_NextPage"] = "下一页",
            ["MudPagination_PageIndex"] = "第 {0} 页",
            ["MudPagination_PreviousPage"] = "上一页",
            ["MudTablePager_FirstPage"] = "第一页",
            ["MudTablePager_LastPage"] = "最后一页",
            ["MudTablePager_NextPage"] = "下一页",
            ["MudTablePager_PreviousPage"] = "上一页"
        };

    public override LocalizedString this[string key]
        => Translations.TryGetValue(key, out var value)
            ? new LocalizedString(key, value, resourceNotFound: false)
            : base[key];
}
