using Bunit;
using DataTrace.Web.Components.Shared;

namespace DataTrace.Web.Tests;

public sealed class IdleLockTests : WebTestBase
{
    [Fact]
    public void Warning_overlay_states_the_idle_reason_and_offers_a_way_back()
    {
        var cut = Context.Render<IdleLock>(parameters => parameters
            .Add(component => component.WarnMinutes, 25)
            .Add(component => component.GraceMinutes, 5));

        cut.Instance.OnIdleWarning();

        cut.WaitForAssertion(() => Assert.Contains("即将退出登录", cut.Markup));
        Assert.Contains("已 25 分钟没有操作", cut.Markup);
        Assert.Contains("继续使用", cut.Markup);
        Assert.Contains("立即退出", cut.Markup);
    }

    [Fact]
    public void Continuing_dismisses_the_overlay_and_restarts_the_idle_timer()
    {
        var cut = Context.Render<IdleLock>(parameters => parameters
            .Add(component => component.WarnMinutes, 25)
            .Add(component => component.GraceMinutes, 5));
        cut.Instance.OnIdleWarning();
        cut.WaitForAssertion(() => Assert.Contains("即将退出登录", cut.Markup));

        ClickButton(cut, "继续使用");

        cut.WaitForAssertion(() => Assert.DoesNotContain("即将退出登录", cut.Markup));
        Assert.Contains(Context.JSInterop.Invocations, invocation => invocation.Identifier == "dtIdle.reset");
    }
}
