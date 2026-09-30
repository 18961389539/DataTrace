using DataTrace.Web.Services;

using DataTrace.Shared;
namespace DataTrace.Web.Tests;

public class AuditValueFormatterTests
{
    [Fact]
    public void Format_pretty_prints_json_and_preserves_plain_text()
    {
        Assert.Contains("\n", AuditValueFormatter.Format("""{"enabled":true,"count":2}"""));
        Assert.Equal("legacy note", AuditValueFormatter.Format("legacy note"));
        Assert.Equal("（无）", AuditValueFormatter.Format(null));
    }

    [Fact]
    public void GetDifferences_reports_nested_added_removed_and_changed_fields()
    {
        var differences = AuditValueFormatter.GetDifferences(
            """{"limits":{"lower":1,"upper":10},"enabled":true}""",
            """{"limits":{"lower":2},"enabled":true,"note":"manual"}""");

        Assert.Collection(
            differences,
            item =>
            {
                Assert.Equal("limits.lower", item.Path);
                Assert.Equal("1", item.OldValue);
                Assert.Equal("2", item.NewValue);
            },
            item =>
            {
                Assert.Equal("limits.upper", item.Path);
                Assert.Equal("10", item.OldValue);
                Assert.Equal("（不存在）", item.NewValue);
            },
            item =>
            {
                Assert.Equal("note", item.Path);
                Assert.Equal("（不存在）", item.OldValue);
                Assert.Equal("\"manual\"", item.NewValue);
            });
    }

    [Fact]
    public void GetDifferences_ignores_non_object_or_invalid_legacy_values()
    {
        Assert.Empty(AuditValueFormatter.GetDifferences("before", "after"));
        Assert.Empty(AuditValueFormatter.GetDifferences("""{"value":1}""", """[1,2]"""));
    }
}
