using System.Globalization;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Web.Services;

/// <summary>
/// 界面上的原始码（枚举名、审计动作）转成车间人员能直接读懂的中文。
/// 未知值一律回退为原始码，避免新动作上线后显示空白。
/// </summary>
public static class DisplayLabels
{
    /// <summary>
    /// 上下限拼成一行：两端都为空写「未配置」，只有一端时空的那侧用 — 占位。
    /// 数字固定 0.### 且走不变文化。这条以前在工站配置、记录明细、型号限值对话框各写一份，
    /// 占位符（— 与 -）和精度都不一致。
    /// </summary>
    public static string LimitRange(double? lower, double? upper)
        => lower is null && upper is null
            ? "未配置"
            : $"{LimitNum(lower)} ~ {LimitNum(upper)}";

    /// <summary>限值的数字形态；留空用 — 而不是 0，避免"没配"被读成"限值是零"。</summary>
    public static string LimitNum(double? value)
        => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";

    /// <summary>
    /// 路由相对路径 → 页面中文名。顶栏面包屑和"权限不足"页共用这一份，
    /// 否则同一个页面在两个地方叫不同名字。
    /// 未知路径返回 null，由调用方决定兜底文案；<b>null 入参也返回 null</b> ——
    /// "压根没给路径"（比如 /denied 没带 ReturnUrl）不等于"访问的是根路径"，
    /// 否则权限不足页会说"你想打开的「实时看板」"。
    /// </summary>
    public static string? PageTitle(string? relativePath)
    {
        if (relativePath is null)
        {
            return null;
        }

        var path = relativePath.Split('?')[0].Trim('/').ToLowerInvariant();
        if (path.Length == 0)
        {
            return "实时看板";
        }

        if (path.StartsWith("query"))
        {
            return path.Length > 5 ? "数据查询 / 记录明细" : "数据查询";
        }

        return path switch
        {
            "reports" => "报表",
            "simulate" => "PLC 仿真",
            "curve-baseline" => "波形基线",
            "config/plc" => "PLC 连接",
            "config/stations" => "工站配置",
            "config/recipes" => "产品型号",
            "config/settings" => "系统设置",
            "users" => "用户",
            "logs" => "审计日志",
            _ => null
        };
    }

    public static string AuditAction(string? action) => action switch
    {
        "Create" => "新增",
        "Save" => "修改",
        "Delete" => "删除",
        "Login" => "登录",
        "Logout" => "退出",
        "Toggle" => "启停",
        "Switch" => "切换",
        "Export" => "导出",
        _ => NullOr(action)
    };

    public static string EntityType(string? entityType) => entityType switch
    {
        "PlcConnection" => "PLC 连接",
        "Station" => "工站",
        "TagDefinition" => "点位",
        "CurveDefinition" => "曲线",
        "CurveCriterion" => "波形判据",
        "Recipe" => "产品型号",
        "RecipeLimit" => "型号限值",
        "SystemSettings" => "系统设置",
        "User" => "用户",
        _ => NullOr(entityType)
    };

    public static string DataType(PlcDataType dataType) => dataType switch
    {
        PlcDataType.Bool => "布尔",
        PlcDataType.Int16 => "16 位整数",
        PlcDataType.Int32 => "32 位整数",
        PlcDataType.Float => "单精度浮点",
        PlcDataType.Double => "双精度浮点",
        PlcDataType.String => "字符串",
        _ => dataType.ToString()
    };

    /// <summary>字序：A 为最高字节，字母顺序即写入顺序，这里补一句人话说明。</summary>
    public static string WordOrder(FloatWordOrder order) => order switch
    {
        FloatWordOrder.ABCD => "ABCD 大端正序",
        FloatWordOrder.BADC => "BADC 大端交换字节",
        FloatWordOrder.CDAB => "CDAB 小端交换字",
        FloatWordOrder.DCBA => "DCBA 小端逆序",
        _ => order.ToString()
    };

    private static string NullOr(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}
