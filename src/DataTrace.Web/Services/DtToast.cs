using MudBlazor;

namespace DataTrace.Web.Services;

/// <summary>
/// 全局提示条。相比直接用 ISnackbar，这里统一了三件事：
/// 按严重度分档的停留时长（车间现场要低头看屏再抬头，中文长句 3 秒读不完）、
/// 同文案去重（连点两次按钮不叠出两条相同提示，而是重置计时）、
/// 以及错误类必须点掉才消失（MudBlazor 7 的 Snackbar 没有关闭按钮，用 RequireInteraction 代替）。
/// </summary>
public sealed class DtToast(ISnackbar snackbar)
{
    public void Add(string message, Severity severity)
    {
        // 以文案为键：先撤掉正在显示的同一条，再重新弹出，等效于重置自动消失计时。
        snackbar.RemoveByKey(message);
        snackbar.Add(message, severity, options =>
        {
            options.VisibleStateDuration = severity switch
            {
                Severity.Error => 15000,
                Severity.Warning => 8000,
                Severity.Info => 5000,
                _ => 4000
            };
            // 超规格、写库失败这类错误一闪而过会被当成没发生，要求交互后才收起。
            options.RequireInteraction = severity == Severity.Error;
        }, message);
    }

    public void Clear() => snackbar.Clear();
}
