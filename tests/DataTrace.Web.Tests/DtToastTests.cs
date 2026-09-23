using Moq;

namespace DataTrace.Web.Tests;

/// <summary>提示条封装：严重度决定停留时长与是否需要点掉。</summary>
public class DtToastTests
{
    private static ToastSpy Emit(Severity severity)
    {
        var spy = new ToastSpy();
        new DataTrace.Web.Services.DtToast(spy.Mock.Object).Add("产线异常", severity);
        return spy;
    }

    [Theory]
    [InlineData(Severity.Error, 15000)]
    [InlineData(Severity.Warning, 8000)]
    [InlineData(Severity.Info, 5000)]
    [InlineData(Severity.Normal, 4000)]
    public void VisibleDurationScalesWithSeverity(Severity severity, int expectedMs)
        => Assert.Equal(expectedMs, Emit(severity).OptionsOf().VisibleStateDuration);

    [Theory]
    [InlineData(Severity.Error, true)]
    [InlineData(Severity.Warning, false)]
    [InlineData(Severity.Info, false)]
    [InlineData(Severity.Normal, false)]
    public void OnlyErrorsRequireInteraction(Severity severity, bool expected)
        => Assert.Equal(expected, Emit(severity).OptionsOf().RequireInteraction);

    [Fact]
    public void MessageIsUsedAsDeduplicationKey()
    {
        var spy = new ToastSpy();
        var toast = new DataTrace.Web.Services.DtToast(spy.Mock.Object);

        toast.Add("重复提示", Severity.Info);
        toast.Add("重复提示", Severity.Info);

        // 同文案以 key 去重：先撤再弹，等于重置计时，而不是叠出两条。
        spy.Mock.Verify(s => s.RemoveByKey("重复提示"), Times.Exactly(2));
        Assert.Equal(2, spy.Shown.Count);
        Assert.All(spy.Shown, x => Assert.Equal("重复提示", x.Key));
    }

    [Fact]
    public void ClearForwardsToSnackbar()
    {
        var spy = new ToastSpy();
        var toast = new DataTrace.Web.Services.DtToast(spy.Mock.Object);

        toast.Add("先攒一条", Severity.Info);
        toast.Clear();

        spy.Mock.Verify(s => s.Clear(), Times.Once);
    }

    [Fact]
    public void SeverityIsPassedThroughToShowSnackbar()
    {
        var spy = Emit(Severity.Warning);

        Assert.Equal("产线异常", spy.LastMessage);
        Assert.Equal(Severity.Warning, spy.LastSeverity);
    }
}
