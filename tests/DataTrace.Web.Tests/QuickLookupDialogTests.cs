using DataTrace.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DataTrace.Web.Tests;

public sealed class QuickLookupDialogTests : WebTestBase
{
    private async Task<QuickLookupDialog.LookupRequest?> SubmitAsync(string value, string buttonText)
    {
        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        var dialog = await Context.Services.GetRequiredService<IDialogService>()
            .ShowAsync<QuickLookupDialog>("快速查找");
        provider.Render();

        provider.Find("input").Input(value);
        provider.Render();
        provider.FindAll("button").Single(button => button.TextContent.Trim() == buttonText).Click();

        var result = await dialog.Result;
        return result?.Data as QuickLookupDialog.LookupRequest;
    }

    [Fact]
    public async Task Serial_shaped_input_is_looked_up_as_a_serial()
    {
        var request = await SubmitAsync("20261012-002061", "查询");

        Assert.NotNull(request);
        Assert.True(request.IsSerial);
        Assert.Equal("20261012-002061", request.Value);
    }

    [Fact]
    public async Task Other_input_defaults_to_a_pallet_code()
    {
        var request = await SubmitAsync("PLT-0007", "查询");

        Assert.NotNull(request);
        Assert.False(request.IsSerial);
    }

    [Fact]
    public async Task Explicit_pallet_choice_wins_over_the_serial_shape()
    {
        var request = await SubmitAsync("20261012-002061", "按托盘码");

        Assert.NotNull(request);
        Assert.False(request.IsSerial);
    }
}
