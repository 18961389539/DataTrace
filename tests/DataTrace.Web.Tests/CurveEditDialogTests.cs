using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Dialogs;
using DataTrace.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DataTrace.Web.Tests;

/// <summary>
/// 曲线编辑对话框：新建时的品牌化默认地址、地址校验分路，以及保存时交回给页面的那份曲线。
/// </summary>
/// <remarks>
/// 走真对话框路径（真 DialogService + MudDialogProvider），否则测不到对话框自己算出来的默认地址，
/// 也测不到"提交被拦下"是真的没有关闭窗口。
/// </remarks>
public class CurveEditDialogTests : WebTestBase
{
    private static CurveDefinition Existing() => new()
    {
        Id = 11,
        StationId = 10,
        Code = "ST010_PD",
        Name = "位移压力曲线",
        PointCount = 50,
        PositionIndex = 1,
        Enabled = true,
        Series =
        [
            new CurveSeries
            {
                Id = 21, Name = "压力", Role = SeriesRole.Y, StartAddress = "D3000",
                DataType = PlcDataType.Float, StrideWords = 2, Scale = 2, Offset = 1.5, Unit = "kN"
            },
            new CurveSeries
            {
                Id = 22, Name = "位移", Role = SeriesRole.X, StartAddress = "D3200",
                DataType = PlcDataType.Float, StrideWords = 2, Unit = "mm"
            }
        ]
    };

    private static IReadOnlyList<CurveDefinition> Siblings() => [Existing()];

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> OpenAsync(
        CurveDefinition curve,
        bool isNew = false,
        PlcBrand? brand = PlcBrand.MitsubishiMc3E)
    {
        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.RenderComponent<MudDialogProvider>();
        var dialogs = Context.Services.GetRequiredService<IDialogService>();
        var reference = await dialogs.ShowAsync<CurveEditDialog>("曲线", new DialogParameters
        {
            ["Curve"] = curve,
            ["IsNew"] = isNew,
            ["Brand"] = brand,
            ["Siblings"] = Siblings()
        });
        provider.Render();
        return (provider, reference);
    }

    private static void ClickDialogButton(IRenderedFragment provider, string text)
    {
        var button = provider.FindAll("button").FirstOrDefault(b => b.TextContent.Contains(text));
        Assert.NotNull(button);
        button.Click();
    }

    /// <summary>点保存后断言"结果没有交回页面"——对话框里的红字拦住了这次提交。</summary>
    private static void AssertStillOpen(IDialogReference reference)
        => Assert.False(reference.Result.IsCompleted, "校验没通过却把结果交回了页面");

    private static string ValueOf(IRenderedFragment provider, string label)
    {
        var labelElement = provider.FindAll("label").First(l => l.TextContent.Contains(label));
        return provider.Find($"#{labelElement.GetAttribute("for")}").GetAttribute("value") ?? "";
    }

    [Theory]
    [InlineData(PlcBrand.MitsubishiMc3E, "D2000", "D2400")]
    [InlineData(PlcBrand.SiemensS7, "MW2000", "MW2400")]
    [InlineData(PlcBrand.OmronFins, "D2000", "D2400")]
    [InlineData(PlcBrand.ModbusTcp, "42001", "42401")]
    public async Task New_curve_prefills_addresses_for_the_plc_brand(PlcBrand brand, string y, string x)
    {
        var (provider, _) = await OpenAsync(
            new CurveDefinition { StationId = 10, PointCount = 50, PositionIndex = 1 },
            isNew: true,
            brand: brand);

        // 预填一个非法地址等于把"保存被拦下"变成常态，用户只会以为按钮坏了。
        Assert.Equal(y, ValueOf(provider, "Y 起始地址"));
        Assert.Equal(x, ValueOf(provider, "X 起始地址"));
    }

    [Fact]
    public async Task Existing_curve_keeps_the_stored_addresses_instead_of_the_brand_defaults()
    {
        // 西门子品牌 + 库里存着三菱写法的地址：编辑时必须原样载入，
        // 不能因为"这套默认值不是我给的"就把用户填过的地址改掉。
        var (provider, _) = await OpenAsync(Existing(), brand: PlcBrand.SiemensS7);

        Assert.Equal("D3000", ValueOf(provider, "Y 起始地址"));
        Assert.Equal("D3200", ValueOf(provider, "X 起始地址"));
        // 已存下的曲线进对话框就校验一遍：按西门子的写法，这两条地址是错的。
        Assert.Contains("地址写法不对", provider.Markup);
    }

    [Fact]
    public async Task Wrong_brand_address_blocks_saving()
    {
        var curve = Existing();
        curve.Series.Single(s => s.Role == SeriesRole.Y).StartAddress = "D3000";
        var (provider, reference) = await OpenAsync(curve, brand: PlcBrand.SiemensS7);

        ClickDialogButton(provider, "保存并下发");

        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Bit_address_blocks_saving()
    {
        var curve = Existing();
        // 位地址进不了读计划（只收字地址）：采集时会静默读回 0，现场只看到"这条曲线全是 0"。
        curve.Series.Single(s => s.Role == SeriesRole.Y).StartAddress = "M100";
        var (provider, reference) = await OpenAsync(curve);

        Assert.Contains("位地址暂不支持", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Empty_address_blocks_saving()
    {
        var curve = Existing();
        curve.Series.Single(s => s.Role == SeriesRole.X).StartAddress = "  ";
        var (provider, reference) = await OpenAsync(curve);

        Assert.Contains("X 起始地址不能为空", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Out_of_range_point_count_blocks_saving()
    {
        var curve = Existing();
        curve.PointCount = 1;
        var (provider, reference) = await OpenAsync(curve);

        Assert.Contains("至少", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Duplicated_code_is_rejected()
    {
        var curve = Existing();
        curve.Id = 99;
        curve.Code = "ST010_PD";
        var (provider, reference) = await OpenAsync(curve);

        Assert.Contains("已存在", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Saving_keeps_the_series_identity_and_the_hidden_decode_parameters()
    {
        var (provider, reference) = await OpenAsync(Existing());

        ClickDialogButton(provider, "保存并下发");

        var result = await reference.Result;
        var edited = Assert.IsType<CurveDefinition>(result!.Data);
        var y = edited.Series.Single(s => s.Role == SeriesRole.Y);
        // 序列主键要带回去：仓储按角色整体替换 Series，主键丢了就是删一条再插一条。
        Assert.Equal(21, y.Id);
        // Scale/Offset 没有界面入口，但采集解码会用到：重建 Series 时不能把配置悄悄改回默认。
        Assert.Equal(2d, y.Scale);
        Assert.Equal(1.5d, y.Offset);
        Assert.Equal("D3000", y.StartAddress);
        Assert.Equal("D3200", edited.Series.Single(s => s.Role == SeriesRole.X).StartAddress);
    }

    [Fact]
    public async Task Saving_trims_the_typed_values()
    {
        var curve = Existing();
        curve.Code = "  ST010_PD  ";
        curve.Name = " 位移压力曲线 ";
        var (provider, reference) = await OpenAsync(curve);

        ClickDialogButton(provider, "保存并下发");

        var result = await reference.Result;
        var edited = Assert.IsType<CurveDefinition>(result!.Data);
        Assert.Equal("ST010_PD", edited.Code);
        Assert.Equal("位移压力曲线", edited.Name);
    }

    [Fact]
    public async Task Cancel_returns_no_result_so_the_page_writes_nothing()
    {
        var (provider, reference) = await OpenAsync(Existing());

        ClickDialogButton(provider, "取消");

        var result = await reference.Result;
        Assert.Null(result?.Data);
    }
}