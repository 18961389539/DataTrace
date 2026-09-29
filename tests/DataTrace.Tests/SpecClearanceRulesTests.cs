using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;

namespace DataTrace.Tests;

public class SpecClearanceRulesTests
{
    [Fact]
    public void Nearest_ok_reading_and_the_ten_percent_band_are_named()
    {
        var clearance = SpecClearanceRules.Of(
        [
            new InSpecReading(10, 0, 100),
            new InSpecReading(92, 0, 100),
            new InSpecReading(50, 0, 100)
        ]);

        Assert.Equal(8, clearance!.Nearest);
        Assert.Equal(2, clearance.NearCount);
        Assert.Equal("合格件里最近离红线还有 8，2 件落在靠红线的一成内", clearance.Text);
    }

    [Fact]
    public void A_reading_sitting_on_the_limit_is_already_against_the_line()
    {
        var clearance = SpecClearanceRules.Of([new InSpecReading(20, 0, 20)]);

        Assert.Equal(0, clearance!.Nearest);
        Assert.Equal("合格件里最近已经贴着红线，1 件落在靠红线的一成内", clearance.Text);
    }

    [Fact]
    public void One_sided_limit_has_no_band()
    {
        var clearance = SpecClearanceRules.Of([new InSpecReading(12, null, 20)]);

        Assert.Equal(8, clearance!.Nearest);
        Assert.False(clearance.HasBand);
        Assert.Equal("合格件里最近离红线还有 8", clearance.Text);
    }

    [Fact]
    public void Values_outside_the_stored_limits_are_skipped()
    {
        Assert.Null(SpecClearanceRules.Of(
        [
            new InSpecReading(25, 0, 20),
            new InSpecReading(12, null, null)
        ]));
    }
}
