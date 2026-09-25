using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>
/// 配置页的地址校验：位地址解析得出来但采集读不到，必须在保存前拦住。
/// </summary>
/// <remarks>
/// 采集侧此前对位地址是静默跳过：点位恒读 0、Bool 恒 false、工站永不触发。
/// 现场只会怀疑"设备/程序不对"，所以配置页要把话说在前面。
/// </remarks>
public class AddressValidationPageTests : WebTestBase
{
    private static PlcConnection Mitsubishi() => new()
    {
        Id = 1,
        Name = "一号线主 PLC",
        Brand = PlcBrand.MitsubishiMc3E,
        Host = "192.168.1.10",
        Port = 5000,
        Enabled = true
    };

    private static PlcConnection Siemens() => new()
    {
        Id = 2,
        Name = "西门子主 PLC",
        Brand = PlcBrand.SiemensS7,
        Host = "192.168.1.20",
        Port = 102,
        Enabled = true
    };

    private static Station Station(int plcId, int id = 10, string code = "ST010", string name = "上料工站", int sequence = 10) => new()
    {
        Id = id,
        PlcConnectionId = plcId,
        Code = code,
        Name = name,
        Sequence = sequence,
        TriggerAddress = "D1000",
        TriggerValue = 1,
        PalletCodeAddress = "D1010",
        PalletCodeLength = 16,
        PositionCount = 1,
        Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }],
        Enabled = true
    };

    /// <summary>一条已存在的曲线：用来验证「编辑」是把现值载入表单、不是按默认值重建。</summary>
    private static CurveDefinition ExistingCurve() => new()
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
            new CurveSeries { Id = 21, Name = "压力", Role = SeriesRole.Y, StartAddress = "D3000", DataType = PlcDataType.Float, StrideWords = 2, Unit = "kN" },
            new CurveSeries { Id = 22, Name = "位移", Role = SeriesRole.X, StartAddress = "D3200", DataType = PlcDataType.Float, StrideWords = 2, Unit = "mm" }
        ]
    };

    private IRenderedComponent<Stations> RenderStations()
    {
        Context.RenderComponent<MudBlazor.MudPopoverProvider>();
        return Context.RenderComponent<Stations>();
    }

    private void SeedStationPage(PlcConnection? plc = null, CurveDefinition? curve = null, params Station[] extraStations)
    {
        var connection = plc ?? Mitsubishi();
        var station = Station(connection.Id);
        if (curve is not null)
        {
            station.Curves = [curve];
        }

        Config.Snapshot = new AppConfigurationSnapshot
        {
            PlcConnections = [connection],
            Stations = [station, .. extraStations]
        };
    }

    [Fact]
    public void Bit_trigger_address_shows_error_and_blocks_saving()
    {
        SeedStationPage();
        var cut = RenderStations();

        TypeInto(cut, "触发 / 回写地址", "M100");

        // 报错要说明"为什么不行"和"改成什么"，而不是只说一句非法。
        Assert.Contains("位地址暂不支持", cut.Markup);
        Assert.Contains("非 0 即 true", cut.Markup);

        // 保存按钮仍然可点，但点下去不能落库。
        ClickButton(cut, "保存");
        Assert.DoesNotContain(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Correct_trigger_address_saves()
    {
        SeedStationPage();
        var cut = RenderStations();

        TypeInto(cut, "触发 / 回写地址", "D1200");
        ClickButton(cut, "保存");

        Assert.Contains(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
        Assert.DoesNotContain("位地址暂不支持", cut.Markup);
    }

    [Fact]
    public void Bit_pallet_code_address_is_rejected()
    {
        SeedStationPage();
        var cut = RenderStations();

        TypeInto(cut, "托盘码地址", "M200");

        Assert.Contains("位地址暂不支持", cut.Markup);
        ClickButton(cut, "保存");
        Assert.DoesNotContain(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Address_is_validated_against_the_brand_of_the_selected_plc()
    {
        var siemens = new PlcConnection
        {
            Id = 2,
            Name = "西门子主 PLC",
            Brand = PlcBrand.SiemensS7,
            Host = "192.168.1.20",
            Port = 102,
            Enabled = true
        };
        // 这条工站是按三菱写的（触发与托盘码都是 D 区），挂到西门子上就是两条非法配置。
        SeedStationPage(siemens);
        var cut = RenderStations();

        // 老配置不该"看着没事"：进页面按所属 PLC 的品牌重算一遍，并给出该品牌的写法。
        Assert.Contains("地址写法不对", cut.Markup);
        Assert.Contains("西门子请写 DB1.DBW0", cut.Markup);

        // 改好触发地址但托盘码还非法时仍不能保存。
        TypeInto(cut, "触发 / 回写地址", "DB1.DBW0");
        ClickButton(cut, "保存");
        Assert.DoesNotContain(nameof(IConfigRepository.SaveStationAsync), Config.Calls);

        // 两个地址都改成西门子写法即可保存。
        TypeInto(cut, "托盘码地址", "DB1.DBW20");
        ClickButton(cut, "保存");
        Assert.Contains(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Curve_form_defaults_follow_the_plc_brand()
    {
        SeedStationPage(Siemens());
        var cut = RenderStations();

        // 曲线表单在「曲线」页签里：MudTabs 默认只渲染当前页签的内容。
        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("曲线")).Click();

        // 默认起止地址按西门子的写法给，不再一进页面就是非法地址。
        Assert.Equal("MW2000", InputForLabel(cut, "Y 起始地址").GetAttribute("value"));
        Assert.Equal("MW2400", InputForLabel(cut, "X 起始地址").GetAttribute("value"));

        TypeInto(cut, "曲线编码", "ST010_PD2");
        ClickButton(cut, "添加曲线");

        Assert.Contains(nameof(IConfigRepository.SaveCurveAsync), Config.Calls);
    }

    [Fact]
    public void Curve_form_rejects_wrong_brand_address_with_a_toast()
    {
        SeedStationPage(Siemens());
        var cut = RenderStations();
        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("曲线")).Click();

        TypeInto(cut, "曲线编码", "ST010_PD3");
        TypeInto(cut, "Y 起始地址", "D2000");

        ClickButton(cut, "添加曲线");

        Assert.DoesNotContain(nameof(IConfigRepository.SaveCurveAsync), Config.Calls);
        Assert.Equal("地址写法不对，西门子请写 DB1.DBW0、DB1.DBD4、MW10 这类地址", Toast.LastMessage);
    }

    [Fact]
    public void Curve_existing_definition_can_be_edited_in_place()
    {
        SeedStationPage(curve: ExistingCurve());
        var cut = RenderStations();
        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("曲线")).Click();

        ClickButton(cut, "编辑");
        // 表单载入了这条曲线的现值，而不是新增用的默认值。
        Assert.Equal("D3000", InputForLabel(cut, "Y 起始地址").GetAttribute("value"));

        TypeInto(cut, "Y 起始地址", "D3100");
        ClickButton(cut, "保存修改");

        Assert.Contains(nameof(IConfigRepository.SaveCurveAsync), Config.Calls);
        var saved = Assert.Single(Config.SavedCurves);
        Assert.Equal(11, saved.Id);
        Assert.Equal("D3100", saved.Series.Single(s => s.Role == SeriesRole.Y).StartAddress);
        // X 序列原样带过去，不会被当成新增重置。
        Assert.Equal("D3200", saved.Series.Single(s => s.Role == SeriesRole.X).StartAddress);
    }

    [Fact]
    public void Trigger_value_cannot_be_a_write_back_code()
    {
        SeedStationPage();
        var cut = RenderStations();

        // 触发与回写共用一个寄存器：取 2 会让触发位永远清不掉、工站被反复触发。
        TypeInto(cut, "触发值", "2");

        Assert.Contains("响应码", cut.Markup);
        ClickButton(cut, "保存");
        Assert.DoesNotContain(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Duplicate_sequence_is_rejected_and_line_without_last_station_is_flagged()
    {
        SeedStationPage(extraStations: Station(1, id: 11, code: "ST020", name: "压装工站", sequence: 20));

        var cut = RenderStations();

        // 没配末站：保存前后都要能看到后果说明（只提示、不拦保存）。
        Assert.Contains("没有任何启用的末站", cut.Markup);

        TypeInto(cut, "产线顺序", "20");

        Assert.Contains("已被工站 ST020 占用", cut.Markup);
        ClickButton(cut, "保存");
        Assert.DoesNotContain(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Station_form_exposes_occupancy_and_tag_visibility()
    {
        SeedStationPage();
        var cut = RenderStations();

        // 有料地址：界面可配（留空即恒为有料），保存时随工站一起下发。
        TypeInto(cut, "有料地址（可留空）", "D1099");
        ClickButton(cut, "保存");

        var station = Assert.Single(Config.SavedStations);
        Assert.Equal("D1099", station.Positions.Single().OccupiedAddress);

        // 点位列表要能看出"停用/位置"，否则历史里 Enabled=false 的点位会莫名其妙不采集。
        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("点位")).Click();
        Assert.Contains("位置", cut.Markup);
        Assert.Contains("工站级", cut.Markup);
    }

    [Fact]
    public void Audit_failure_is_reported_separately_from_a_successful_save()
    {
        SeedStationPage();
        // 审计写库失败：配置其实已经保存并下发，提示必须把两件事分开说。
        Audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("审计库不可用"));

        var cut = RenderStations();
        ClickButton(cut, "保存");

        Assert.Contains(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
        Assert.Contains(Toast.Messages, m => m.Contains("审计记录失败") && m.Contains("审计库不可用"));
        Assert.Contains(Toast.Messages, m => m.Contains("已保存并下发采集器"));
    }

    [Fact]
    public void New_plc_connection_starts_without_a_heartbeat_address()
    {
        Context.RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = Context.RenderComponent<PlcConfig>();

        ClickButton(cut, "新增连接");

        // 心跳是写给某一台 PLC 的地址：预填 D0 换个品牌（西门子/Modbus）就是一条必然写不进去的配置。
        var (title, parameters) = Assert.Single(Dialogs.Shown);
        Assert.Equal("新增 PLC 连接", title);
        Assert.Equal("", parameters.Get<string>("HeartbeatAddress"));
    }
}

