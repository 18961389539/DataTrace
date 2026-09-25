using DataTrace.Domain.Enums;
using DataTrace.Plc.Addresses;

namespace DataTrace.Tests;

/// <summary>
/// 配置期地址校验：位地址必须被明确拒绝（读计划只收字地址，否则会被静默丢弃），
/// 非法写法要给出可直接照抄的例子。
/// </summary>
public class PlcAddressValidatorTests
{
    [Theory]
    [InlineData(PlcBrand.MitsubishiMc3E, "M100")]
    [InlineData(PlcBrand.MitsubishiMc3E, "Y0")]
    [InlineData(PlcBrand.MitsubishiMc3E, "D100.3")]
    [InlineData(PlcBrand.SiemensS7, "DB1.DBX0.0")]
    [InlineData(PlcBrand.SiemensS7, "M10.2")]
    [InlineData(PlcBrand.ModbusTcp, "10001")]
    [InlineData(PlcBrand.ModbusTcp, "1")]
    [InlineData(PlcBrand.OmronFins, "CIO0.0")]
    [InlineData(PlcBrand.Simulator, "X1A")]
    public void Bit_addresses_are_rejected_with_a_chinese_hint(PlcBrand brand, string text)
    {
        Assert.False(PlcAddressValidator.TryValidate(brand, text, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("位地址暂不支持", error);
        Assert.Contains("非 0 即 true", error);
    }

    [Theory]
    [InlineData(PlcBrand.MitsubishiMc3E, "D100", OffsetUnit.Word)]
    [InlineData(PlcBrand.MitsubishiMc3E, "ZR100", OffsetUnit.Word)]
    [InlineData(PlcBrand.SiemensS7, "DB1.DBW0", OffsetUnit.Byte)]
    [InlineData(PlcBrand.SiemensS7, "DB108.DBW4", OffsetUnit.Byte)]
    [InlineData(PlcBrand.ModbusTcp, "40001", OffsetUnit.Word)]
    [InlineData(PlcBrand.OmronFins, "D100", OffsetUnit.Word)]
    public void Word_addresses_pass_and_carry_their_offset_unit(PlcBrand brand, string text, OffsetUnit unit)
    {
        Assert.True(PlcAddressValidator.TryValidate(brand, text, out var address, out var error));
        Assert.Null(error);
        Assert.Equal(unit, address.OffsetUnit);
    }

    [Fact]
    public void Wrong_syntax_for_the_brand_is_rejected_with_examples()
    {
        // 把三菱地址填到西门子 PLC 上：必须报错，并给出该品牌的写法示例。
        Assert.False(PlcAddressValidator.TryValidate(PlcBrand.SiemensS7, "D100", out _, out var siemens));
        Assert.Contains("DB1.DBW0", siemens);

        Assert.False(PlcAddressValidator.TryValidate(PlcBrand.MitsubishiMc3E, "40001", out _, out var mitsubishi));
        Assert.Contains("D100", mitsubishi);

        Assert.False(PlcAddressValidator.TryValidate(PlcBrand.ModbusTcp, "D100", out _, out var modbus));
        Assert.Contains("40001", modbus);

        Assert.False(PlcAddressValidator.TryValidate(PlcBrand.OmronFins, "D1A", out _, out var omron));
        Assert.Contains("CIO100", omron);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_is_rejected(string? text)
    {
        Assert.False(PlcAddressValidator.TryValidate(PlcBrand.MitsubishiMc3E, text, out _, out var error));
        Assert.Equal("地址不能为空", error);
    }

    [Fact]
    public void Simulator_uses_mitsubishi_syntax()
    {
        Assert.True(PlcAddressValidator.TryValidate(PlcBrand.Simulator, "D100", out _, out _));
        Assert.False(PlcAddressValidator.TryValidate(PlcBrand.Simulator, "M100", out _, out _));
    }

    [Fact]
    public void Parser_lookup_covers_every_brand_with_its_own_syntax()
    {
        Assert.IsType<SiemensAddressParser>(AddressParsers.For(PlcBrand.SiemensS7));
        Assert.IsType<ModbusAddressParser>(AddressParsers.For(PlcBrand.ModbusTcp));
        Assert.IsType<OmronAddressParser>(AddressParsers.For(PlcBrand.OmronFins));
        Assert.IsType<MitsubishiAddressParser>(AddressParsers.For(PlcBrand.MitsubishiMc3E));
        Assert.IsType<MitsubishiAddressParser>(AddressParsers.For(PlcBrand.Simulator));
    }
}