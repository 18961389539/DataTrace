using DataTrace.Web.Components.Dialogs;
using MudBlazor;

namespace DataTrace.Web.Services;

/// <summary>
/// 危险操作的统一确认入口。页面上原先散落着 20 处 <see cref="IDialogService.ShowMessageBox"/>，
/// 它们只有一个「确认」按钮，按错了没有回头路；这里统一换成带「勾选我了解」复选框的第二道门，
/// 未勾选时确认按钮不可点。返回值沿用 MessageBox 语义：true 表示用户确认。
/// </summary>
public static class DangerConfirmExtensions
{
    /// <param name="acknowledge">复选框文案，写清"我了解什么后果"。</param>
    /// <param name="yesText">确认按钮文案；保留各调用点原有的措辞（如"确认删除""仍要切换"）。</param>
    public static async Task<bool> ConfirmDangerAsync(
        this IDialogService dialogs,
        string title,
        string message,
        string acknowledge,
        string yesText = "确认删除",
        string cancelText = "取消")
    {
        var parameters = new DialogParameters<ConfirmDangerDialog>
        {
            { x => x.Title, title },
            { x => x.Message, message },
            { x => x.Acknowledge, acknowledge },
            { x => x.YesText, yesText },
            { x => x.CancelText, cancelText }
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.ExtraSmall,
            CloseOnEscapeKey = true,
            FullWidth = true
        };

        var dialog = await dialogs.ShowAsync<ConfirmDangerDialog>(title, parameters, options);
        var result = await dialog.Result;
        return result is { Canceled: false };
    }
}