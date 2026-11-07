using DataTrace.Web.Services;

namespace DataTrace.Web.Tests;

/// <summary>托盘只在交互式 Windows 进程出现；提示文字不能长过通知区域的上限。</summary>
public class TrayIconHostTests
{
    [Fact]
    public void ShowsOnlyForAnInteractiveWindowsProcess()
    {
        Assert.True(TrayIconHost.ShouldShow(isWindows: true, userInteractive: true, isWindowsService: false, enabled: true));
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    public void HidesWhenTheDesktopIsUnavailableOrDisabled(bool windows, bool interactive, bool service, bool enabled)
    {
        Assert.False(TrayIconHost.ShouldShow(windows, interactive, service, enabled));
    }

    [Fact]
    public void TipIncludesThePageAddress()
    {
        Assert.Equal("DataTrace", TrayIconHost.FormatTip(null));
        Assert.Equal("DataTrace", TrayIconHost.FormatTip("  "));
        Assert.Equal("DataTrace http://localhost:5080/", TrayIconHost.FormatTip("http://localhost:5080/"));
    }

    [Fact]
    public void TipStaysWithinTheShellLimit()
    {
        var tip = TrayIconHost.FormatTip("http://localhost/" + new string('a', 200));

        Assert.Equal(127, tip.Length);
        Assert.EndsWith("…", tip);
    }
}
