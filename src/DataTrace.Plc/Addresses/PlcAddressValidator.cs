using DataTrace.Domain.Enums;

namespace DataTrace.Plc.Addresses;

/// <summary>
/// 配置期地址校验：把"只有采集时才暴露"的坏地址挡在保存之前。
/// </summary>
/// <remarks>
/// 最典型的是位地址：三菱的 M100、欧姆龙的 CIO0.0、Modbus 的线圈/离散量、西门子的 DB1.DBX0.0
/// 都能被解析器认出来，但读计划只收字地址，位地址会被<b>静默丢弃</b> ——
/// 采集时该点位读回 0、Bool 永远为 false，现场看到的是"值不对"而不是"配错了"。
/// 这里明确拒绝，并直接告诉用户该改成什么写法。
/// </remarks>
public static class PlcAddressValidator
{
    /// <summary>校验地址文本，失败时 <paramref name="error"/> 是可直接展示给用户的中文原因。</summary>
    public static bool TryValidate(PlcBrand brand, string? text, out string? error)
        => TryValidate(brand, text, out _, out error);

    /// <summary>校验地址文本并返回解析结果。</summary>
    public static bool TryValidate(PlcBrand brand, string? text, out PlcAddress address, out string? error)
    {
        address = null!;
        error = null;

        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            error = "地址不能为空";
            return false;
        }

        if (!AddressParsers.For(brand).TryParse(trimmed, out address))
        {
            error = $"地址写法不对，{Examples(brand)}";
            return false;
        }

        if (address.IsBit)
        {
            error = $"位地址暂不支持，{BitHint(brand)}";
            return false;
        }

        return true;
    }

    private static string Examples(PlcBrand brand) => brand switch
    {
        PlcBrand.SiemensS7 => "西门子请写 DB1.DBW0、DB1.DBD4、MW10 这类地址",
        PlcBrand.ModbusTcp => "Modbus 请写 40001（保持寄存器）或 30001（输入寄存器）这类地址",
        PlcBrand.OmronFins => "欧姆龙请写 D100、CIO100、W0、H10 这类地址",
        _ => "三菱请写 D100、W1A、ZR100、SD10 这类字地址"
    };

    private static string BitHint(PlcBrand brand) => brand switch
    {
        PlcBrand.SiemensS7 => "Bool 请改用字地址（如 DB1.DBW0 或 MW10，非 0 即 true）",
        PlcBrand.ModbusTcp => "Bool 请改用保持寄存器字地址（如 40001，非 0 即 true）",
        PlcBrand.OmronFins => "Bool 请改用 DM 字地址（如 D100，非 0 即 true）",
        _ => "Bool 请改用字地址（如 D100，非 0 即 true）"
    };
}