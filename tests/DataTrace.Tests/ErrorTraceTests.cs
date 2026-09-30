using DataTrace.Shared;

namespace DataTrace.Tests;

/// <summary>
/// 未处理异常回报码的形状与校验。
/// </summary>
/// <remarks>
/// 生成那一侧只是 Guid 取前 8 位；真正要紧的是校验那一侧 —— 错误页匿名可达，
/// 而码会被原样渲染出去，校验松一格就是多一个反射任意文本的出口。
/// </remarks>
public class ErrorTraceTests
{
    [Fact]
    public void New_code_is_well_formed()
    {
        var code = ErrorTrace.NewCode();

        Assert.Equal(ErrorTrace.CodeLength, code.Length);
        Assert.True(ErrorTrace.IsWellFormed(code));
    }

    [Fact]
    public void Code_is_the_first_eight_hex_digits_of_the_guid_in_upper_case()
    {
        Assert.Equal("01234567", ErrorTrace.FromGuid(Guid.Parse("0123456789abcdef0123456789abcdef")));

        // 小写会被 IsWellFormed 判死，两处口径必须一致，否则页面上会出现自己都不认的码。
        Assert.Equal("ABCDEF00", ErrorTrace.FromGuid(Guid.Parse("abcdef00000000000000000000000000")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("ABCD12345")]
    [InlineData("abcd1234")]
    [InlineData("ABCDEFGH")]
    [InlineData("ABCD 234")]
    [InlineData("<script>")]
    public void Malformed_codes_are_rejected(string? code)
        => Assert.False(ErrorTrace.IsWellFormed(code));

    [Theory]
    [InlineData("00000000")]
    [InlineData("FFFFFFFF")]
    [InlineData("AB12CD34")]
    public void Well_formed_codes_are_accepted(string code)
        => Assert.True(ErrorTrace.IsWellFormed(code));

    [Fact]
    public void Codes_do_not_repeat_within_a_session()
    {
        // 撞码会让两次错误在日志里指向同一行 —— 这是这套机制唯一会把工程师带错方向的失败方式。
        var codes = Enumerable.Range(0, 500).Select(_ => ErrorTrace.NewCode()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(500, codes.Count);
    }
}
