using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Dialogs;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DataTrace.Web.Tests;

/// <summary>
/// 工站配置页的曲线页签：列表只显示摘要，新增与编辑都走对话框。
/// </summary>
/// <remarks>
/// 这里测"页面与对话框的交接"：下发的参数（品牌/同站曲线）、拿到结果后写了什么、
/// 以及列表里直接拨启用开关这条路。字段级校验在 <see cref="CurveEditDialogTests"/> 里测。
/// </remarks>
public class CurvePageTests : WebTestBase
{
    private static PlcConnection Plc(PlcBrand brand = PlcBrand.MitsubishiMc3E) => new()
    {
        Id = 1,
        Name = "一号线主 PLC",
        Brand = brand,
        Host = "192.168.1.10",
        Port = 5000,
        Enabled = true
    };

    private static CurveDefinition Curve(bool enabled = true, int criterionEnabled = 1) => new()
    {
        Id = 11,
        StationId = 10,
        Code = "ST010_PD",
        Name = "位移压力曲线",
        PointCount = 50,
        PositionIndex = 1,
        Enabled = enabled,
        Series =
        [
            new CurveSeries { Id = 21, Name = "压力", Role = SeriesRole.Y, StartAddress = "D3000", DataType = PlcDataType.Float, StrideWords = 2, Unit = "kN" },
            new CurveSeries { Id = 22, Name = "位移", Role = SeriesRole.X, StartAddress = "D3200", DataType = PlcDataType.Float, StrideWords = 2, Unit = "mm" }
        ],
        Criteria =
        [
            new CurveCriterion { Id = 31, CurveDefinitionId = 11, Enabled = true },
            new CurveCriterion { Id = 32, CurveDefinitionId = 11, Enabled = criterionEnabled > 1 }
        ]
    };

    private IRenderedComponent<Stations> RenderStations(CurveDefinition? curve = null, PlcBrand brand = PlcBrand.MitsubishiMc3E)
    {
        var station = new Station
        {
            Id = 10,
            PlcConnectionId = 1,
            Code = "ST010",
            Name = "压装工站",
            Sequence = 10,
            TriggerAddress = "D1000",
            TriggerValue = 1,
            PalletCodeAddress = "D1010",
            PalletCodeLength = 16,
            PositionCount = 1,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }],
            Enabled = true,
            Curves = [curve ?? Curve()]
        };
        Config.Snapshot = new AppConfigurationSnapshot
        {
            PlcConnections = [Plc(brand)],
            Stations = [station]
        };
        Context.RenderComponent<MudPopoverProvider>();
        return Context.RenderComponent<Stations>();
    }

    private static void OpenCurveTab(IRenderedFragment cut)
        => cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("曲线")).Click();

    [Fact]
    public void Curve_list_shows_only_a_summary()
    {
        var cut = RenderStations();
        OpenCurveTab(cut);

        // 编码与名称合并成一行，点数与位置收成小字。
        Assert.Contains("ST010_PD", cut.Markup);
        Assert.Contains("位移压力曲线", cut.Markup);
        Assert.Contains("50 点", cut.Markup);
        // 两条序列各一行显示名称、单位与起始地址，序列名不再各占一列。
        Assert.Contains("Y 压力 kN · D3000", cut.Markup);
        Assert.Contains("X 位移 mm · D3200", cut.Markup);
        // 判据给"几条·启用几条"：全部停用时与没配判据在采集端是同一个行为。
        Assert.Contains("2 条 · 启用 1", cut.Markup);
        // 页面不再常驻编辑表单。
        Assert.Contains("新增曲线", cut.Markup);
    }

    [Fact]
    public void Criteria_summary_says_so_when_none_are_configured()
    {
        var curve = Curve();
        curve.Criteria = [];
        var cut = RenderStations(curve);
        OpenCurveTab(cut);

        Assert.Contains("未配置", cut.Markup);
    }

    [Fact]
    public void Add_curve_opens_the_dialog_with_the_brand_and_station_curves()
    {
        var cut = RenderStations();
        OpenCurveTab(cut);

        ClickButton(cut, "新增曲线");

        var (title, parameters) = Assert.Single(Dialogs.Shown);
        Assert.Equal("新增曲线", title);
        Assert.True(parameters.Get<bool>("IsNew"));
        Assert.Equal(PlcBrand.MitsubishiMc3E, parameters.Get<PlcBrand?>("Brand"));
        // 同站曲线要带上：对话框用它做编码查重。
        Assert.NotNull(parameters.Get<IReadOnlyList<CurveDefinition>>("Siblings"));

        // 对话框没返回结果（用户取消）时不能落库。
        Assert.Empty(Config.SavedCurves);
    }

    [Fact]
    public void Edit_curve_opens_the_dialog_with_a_clone_carrying_both_series()
    {
        var cut = RenderStations();
        OpenCurveTab(cut);

        ClickButton(cut, "编辑");

        var (title, parameters) = Assert.Single(Dialogs.Shown);
        Assert.Equal("编辑曲线 · ST010_PD", title);
        Assert.False(parameters.Get<bool>("IsNew"));
        var curve = Assert.IsType<CurveDefinition>(parameters.Get<CurveDefinition>("Curve"));
        Assert.Equal(11, curve.Id);
        Assert.Equal("D3000", curve.Series.Single(s => s.Role == SeriesRole.Y).StartAddress);
        // 序列必须一起带上：对话框重建 Series 时要用它保住 Id 与 Scale/Offset。
        Assert.Equal(2, curve.Series.Count);
        // 传进去的必须是克隆体：直接交跟踪实体的话，对话框里的每次输入都会改到"库里的那一行"。
        Assert.NotSame(Config.Snapshot.Stations[0].Curves.First(), curve);
    }

    [Fact]
    public void Dialog_result_is_saved_with_audit()
    {
        var cut = RenderStations();
        OpenCurveTab(cut);
        Dialogs.DialogResult = DialogResult.Ok(new CurveDefinition
        {
            Id = 11,
            Code = "ST010_PD",
            Name = "位移压力曲线",
            PointCount = 80,
            PositionIndex = 1,
            Enabled = true,
            Series =
            [
                new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = "D3100", DataType = PlcDataType.Float, StrideWords = 2, Unit = "kN" },
                new CurveSeries { Name = "位移", Role = SeriesRole.X, StartAddress = "D3200", DataType = PlcDataType.Float, StrideWords = 2, Unit = "mm" }
            ]
        });

        ClickButton(cut, "编辑");

        var saved = Assert.Single(Config.SavedCurves);
        Assert.Equal(80, saved.PointCount);
        Assert.Equal("D3100", saved.Series.Single(s => s.Role == SeriesRole.Y).StartAddress);
        // 落库前必须补上工站主键，否则会写出一条挂在别的工站（或 0 号工站）下的曲线。
        Assert.Equal(10, saved.StationId);
        Assert.Contains(Toast.Messages, m => m.Contains("ST010_PD 已保存并下发采集器"));
    }

    [Fact]
    public void Enable_switch_saves_directly_from_the_list()
    {
        var cut = RenderStations();
        OpenCurveTab(cut);

        var toggle = cut.FindAll("input[type=checkbox]").First(i => i.GetAttribute("aria-label")?.Contains("ST010_PD") == true);
        toggle.Change(false);

        var saved = Assert.Single(Config.SavedCurves);
        Assert.False(saved.Enabled);
        // 只改启用也必须把两条序列带上：仓储按角色整体替换 Series，缺了它这次保存会清掉地址。
        Assert.Equal(2, saved.Series.Count);
        Assert.Equal("D3000", saved.Series.Single(s => s.Role == SeriesRole.Y).StartAddress);
        Assert.Contains(Toast.Messages, m => m.Contains("ST010_PD 已停用"));
    }

    [Fact]
    public void Waveform_criteria_button_still_opens_its_own_dialog()
    {
        var cut = RenderStations();
        OpenCurveTab(cut);

        ClickButton(cut, "波形判据");

        var (title, parameters) = Assert.Single(Dialogs.Shown);
        Assert.Contains("波形判据", title);
        Assert.NotNull(parameters.Get<CurveDefinition>("Curve"));
    }
}