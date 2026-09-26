using DataTrace.Collector;

namespace DataTrace.Tests;

public class CsvFieldReaderTests
{
    [Fact]
    public void Quoted_comma_and_escaped_quote_stay_in_the_cell()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("name,force\n\"A,B \"\"x\"\"\",12.5\n");
        Assert.True(CsvFieldReader.TryParse(bytes, out var row, out var error), error);
        Assert.Equal("A,B \"x\"", row["name"]);
        Assert.True(CsvFieldReader.TryReadNumeric(row, "force", out var force));
        Assert.Equal(12.5, force);
    }

    [Fact]
    public void Unit_text_is_not_a_number()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("force\n12 kN\n");
        Assert.True(CsvFieldReader.TryParse(bytes, out var row, out _));
        Assert.False(CsvFieldReader.TryReadNumeric(row, "force", out _));
    }

    [Fact]
    public void Duplicate_header_is_rejected()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("force,force\n1,2\n");
        Assert.False(CsvFieldReader.TryParse(bytes, out _, out var error));
        Assert.Contains("重复", error);
    }
}