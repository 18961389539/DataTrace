using DataTrace.Application.Configuration;
using DataTrace.Collector;
using DataTrace.Plc.Simulator;
using DataTrace.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using AngleSharp.Dom;

namespace DataTrace.Web.Tests;

/// <summary>给 bUnit 装配本应用页面所需的服务替身。</summary>
public abstract class WebTestBase : IDisposable
{
    protected TestContext Context { get; }

    protected FakeConfigRepository Config { get; }

    protected FakeLineSimulator Simulator { get; }

    protected ToastSpy Toast { get; }

    protected DialogSpy Dialogs { get; }

    /// <summary>页面与断言共用同一个模拟 PLC 目录，便于回读寄存器。</summary>
    protected SimulatorCatalog Simulators { get; } = new();

    protected WebTestBase()
    {
        Context = new TestContext();
        Config = new FakeConfigRepository();
        Simulator = new FakeLineSimulator();
        Toast = new ToastSpy();
        Dialogs = new DialogSpy();

        Context.JSInterop.Mode = JSRuntimeMode.Loose;
        Context.Services.AddMudServices();
        Context.Services.AddSingleton<IConfigRepository>(Config);
        Context.Services.AddSingleton<ILineSimulator>(Simulator);
        Context.Services.AddSingleton(Simulators);
        Context.Services.AddSingleton<ISnackbar>(Toast.Mock.Object);
        Context.Services.AddSingleton<IDialogService>(Dialogs.Mock.Object);
        // DtToast 只做"按严重度分档 + 同文案去重"，这里用真身，顺带把它对 ISnackbar 的用法一起测了。
        Context.Services.AddSingleton<DtToast>();
    }

    /// <summary>按可见文字点击按钮，避开 Material 类名随版本漂移的问题。</summary>
    protected void ClickButton(IRenderedFragment cut, string text)
    {
        var button = cut.FindAll("button").SingleOrDefault(b => b.TextContent.Contains(text));
        Assert.NotNull(button);
        button.Click();
    }

    /// <summary>向带 Label 的输入框打字。Immediate 的 MudInput 绑 oninput、其余绑 onchange，两种都照顾到。</summary>
    protected void TypeInto(IRenderedFragment cut, string label, string value)
    {
        var input = InputForLabel(cut, label);
        try
        {
            input.Input(value);
        }
        catch (MissingEventHandlerException)
        {
            input.Change(value);
        }
    }

    /// <summary>按 Label 文本定位输入框（MudInput 会把 label 的 for 指到 input 的 id）。</summary>
    protected IElement InputForLabel(IRenderedFragment cut, string label)
    {
        var found = cut.FindAll("label").SingleOrDefault(l => l.TextContent.Contains(label));
        Assert.NotNull(found);
        var id = found.GetAttribute("for");
        Assert.False(string.IsNullOrEmpty(id));
        return cut.Find($"#{id}");
    }

    /// <summary>把第 n 个开关拨到目标值（MudSwitch 的 input 只监听 onchange，没有 onclick）。</summary>
    protected void ToggleSwitch(IRenderedFragment cut, bool value, int index = 0)
    {
        var switches = cut.FindAll("input[type=checkbox]");
        Assert.True(index < switches.Count, $"页面上只有 {switches.Count} 个开关，取不到第 {index} 个");
        switches[index].Change(value);
    }

    public void Dispose() => Context.Dispose();
}
