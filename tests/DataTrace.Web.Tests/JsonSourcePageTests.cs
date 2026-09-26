using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Dialogs;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DataTrace.Web.Tests;

/// <summary>
/// 工站配置页的点位页签：列表只显示摘要，新增与编辑都走对话框。
/// </summary>
/// <remarks>
/// 这里测的是"页面与对话框的交接"：下发的参数对不对（点位来源/PLC 品牌/同站点位）、
/// 拿到结果后写了什么、以及列表里直接拨启用开关这条路。
/// 字段级校验在 <see cref="TagEditDialogTests"/> 里测，页面不再重复一份。
/// </remarks>
public class JsonSourcePageTests : WebTestBase
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

    private static Station FileStation(string filePath = "D:/data/result.json") => new()
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
        DataFilePath = filePath,
        Tags =
        [
            new TagDefinition
            {
                Id = 31,
                StationId = 10,
                Name = "压力",
                Address = "data.force",
                DataType = PlcDataType.Float,
                Source = TagDataSource.JsonFile,
                Scale = 1,
                Unit = "kN",
                LowerLimit = 5,
                UpperLimit = 20,
                WarningUpperLimit = 18,
                PositionIndex = 1,
                IsRequired = true,
                Enabled = true
            },
            // 同一工站上还有一个走 PLC 的点位：来源是按点位选的，列表里要能分辨。
            new TagDefinition
            {
                Id = 32,
                StationId = 10,
                Name = "温度",
                Address = "D1110",
                DataType = PlcDataType.Float,
                Unit = "℃",
                UpperLimit = 80,
                PositionIndex = 1,
                IsRequired = true,
                Enabled = true
            }
        ]
    };

    private IRenderedComponent<Stations> RenderStations(Station? station = null)
    {
        var target = station ?? FileStation();
        Config.Snapshot = new AppConfigurationSnapshot
        {
            PlcConnections = [Mitsubishi()],
            Stations = [target]
        };
        Context.RenderComponent<MudPopoverProvider>();
        return Context.RenderComponent<Stations>();
    }

    /// <summary>点位清单在「点位」页签里：MudTabs 默认只渲染当前页签的内容。</summary>
    private static void OpenTagTab(IRenderedFragment cut)
        => cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("点位")).Click();

    /// <summary>
    /// 点某一行的按钮。
    /// </summary>
    /// <remarks>
    /// 每行都有「编辑 / 删除」，按文字找会命中多个；行内按钮带 aria-label（含点位名称），
    /// 按它定位既唯一，也顺带证明了读屏能找到"这一行的"按钮。
    /// </remarks>
    private static void ClickByAriaLabel(IRenderedFragment cut, string ariaLabel)
    {
        var buttons = cut.FindAll("button").Where(b => b.GetAttribute("aria-label") == ariaLabel).ToList();
        Assert.Single(buttons);
        buttons[0].Click();
    }

    [Fact]
    public void Tag_list_shows_only_a_summary_and_marks_each_row_source()
    {
        var cut = RenderStations();
        OpenTagTab(cut);

        // 来源是按点位标的：同一张表里要能看出哪个走文件、哪个走 PLC。
        Assert.Contains("来源与地址", cut.Markup);
        Assert.Contains("JSON 文件", cut.Markup);
        Assert.Contains("PLC 寄存器", cut.Markup);
        Assert.Contains("data.force", cut.Markup);
        Assert.Contains("D1110", cut.Markup);
        // 文件源点位的类型只显示「数值」，不该露出 Float 这类 PLC 字宽。
        Assert.DoesNotContain("Float", cut.Markup);

        // 限值只留摘要（带单位），四道限值的完整数字在对话框里；只配一侧的限值不写成"— ~ 18"。
        Assert.Contains("5 ~ 20 kN", cut.Markup);
        Assert.Contains("预警 ≤ 18", cut.Markup);
        // 必填留在名称下的小字里，列表不再单占一列。
        Assert.Contains("必填", cut.Markup);
        Assert.DoesNotContain("工站级", cut.Markup);
        // 汇总一行说清有几个点位走文件，用户不用逐行看来源列。
        Assert.Contains("其中 1 个取自 JSON 文件", cut.Markup);
    }

    [Fact]
    public void Add_tag_opens_the_dialog_with_the_brand_and_the_station_file_path()
    {
        var cut = RenderStations();
        OpenTagTab(cut);

        ClickButton(cut, "新增点位");

        var (title, parameters) = Assert.Single(Dialogs.Shown);
        Assert.Equal("新增点位", title);
        Assert.True(parameters.Get<bool>("IsNew"));
        Assert.Equal(PlcBrand.MitsubishiMc3E, parameters.Get<PlcBrand?>("Brand"));
        // 工站已配的路径要带给对话框：用户选 JSON 来源时它据此判断要不要先补路径。
        Assert.Equal("D:/data/result.json", parameters.Get<string>("DataFilePath"));
        // 同站点的其它点位要带上：对话框用它做名称查重。
        Assert.NotNull(parameters.Get<IReadOnlyList<TagDefinition>>("Siblings"));

        // 编辑对话框没返回结果（用户取消）时不能落库。
        Assert.Empty(Config.SavedTags);
    }

    [Fact]
    public void Edit_tag_opens_the_dialog_with_a_clone_of_the_current_values()
    {
        var cut = RenderStations();
        OpenTagTab(cut);

        ClickByAriaLabel(cut, "编辑点位 压力");

        var (title, parameters) = Assert.Single(Dialogs.Shown);
        Assert.Equal("编辑点位 · 压力", title);
        Assert.False(parameters.Get<bool>("IsNew"));
        var tag = Assert.IsType<TagDefinition>(parameters.Get<TagDefinition>("Tag"));
        Assert.Equal(31, tag.Id);
        Assert.Equal("data.force", tag.Address);
        // 来源是该点位自己的属性，编辑时原样带进对话框。
        Assert.Equal(TagDataSource.JsonFile, tag.Source);
        Assert.Equal(18d, tag.WarningUpperLimit);
        // 传进去的必须是克隆体：直接交跟踪实体的话，对话框里的每次输入都会改到"库里的那一行"。
        Assert.NotSame(Config.Snapshot.Stations[0].Tags.First(), tag);
    }

    [Fact]
    public void Dialog_result_is_saved_with_audit()
    {
        var cut = RenderStations();
        OpenTagTab(cut);
        Dialogs.DialogResult = DialogResult.Ok(new TagDefinition
        {
            Id = 31,
            Name = "压力",
            Address = "force.peak",
            DataType = PlcDataType.Double,
            Unit = "kN",
            LowerLimit = 5,
            UpperLimit = 20,
            IsRequired = true,
            PositionIndex = 1,
            Enabled = true
        });

        ClickByAriaLabel(cut, "编辑点位 压力");

        var saved = Assert.Single(Config.SavedTags);
        Assert.Equal("force.peak", saved.Address);
        // 落库前必须补上工站主键，否则会写出一条挂在别的工站（或 0 号工站）下的点位。
        Assert.Equal(10, saved.StationId);
        Assert.Contains(nameof(IConfigRepository.SaveTagAsync), Config.Calls);
        Assert.Contains(Toast.Messages, m => m.Contains("压力 已保存"));
    }

    [Fact]
    public void Enable_switch_saves_directly_from_the_list()
    {
        var station = FileStation();
        var cut = RenderStations(station);
        OpenTagTab(cut);

        // 列表里第一列开关就是该点位的启用状态（最常改的一个字段，不值得为它开对话框）。
        var toggle = cut.FindAll("input[type=checkbox]").First(i => i.GetAttribute("aria-label")?.Contains("压力") == true);
        toggle.Change(false);

        var saved = Assert.Single(Config.SavedTags);
        Assert.False(saved.Enabled);
        Assert.Equal(31, saved.Id);
        Assert.Contains(nameof(IConfigRepository.SaveTagAsync), Config.Calls);
        Assert.Contains(Toast.Messages, m => m.Contains("压力 已停用"));
    }

    [Fact]
    public void File_source_tag_requires_the_station_file_path_before_saving_the_station()
    {
        var cut = RenderStations(FileStation(filePath: ""));
        OpenTagTab(cut);

        // 有文件源点位却没有路径 = 采集每周期都失败（结果码 9），保存前就拦住。
        Assert.Contains("数据文件路径不能为空", cut.Markup);
        ClickButton(cut, "保存");
        Assert.DoesNotContain(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Station_without_file_source_tags_may_leave_the_path_empty()
    {
        var station = FileStation(filePath: "");
        foreach (var tag in station.Tags)
        {
            tag.Source = TagDataSource.Plc;
            tag.Address = "D1100";
        }

        var cut = RenderStations(station);
        OpenTagTab(cut);

        // 整站都从 PLC 读的工站不该被一条用不上的配置拦住。
        Assert.DoesNotContain("数据文件路径不能为空", cut.Markup);
        Assert.Contains("全部从 PLC 寄存器读", cut.Markup);
        ClickButton(cut, "保存");
        Assert.Contains(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Choosing_a_file_fills_the_path_and_waits_for_station_save()
    {
        var cut = RenderStations(FileStation(filePath: ""));
        OpenTagTab(cut);
        FileDialog.Result = @"D:\device\result.json";

        ClickByAriaLabel(cut, "选择数据文件");

        Assert.Equal("", FileDialog.LastRequest);
        Assert.Contains(@"D:\device\result.json", cut.Markup);
        Assert.DoesNotContain("数据文件路径不能为空", cut.Markup);
        // 选中只填进输入框，配置要等工站上的「保存」。
        Assert.DoesNotContain(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void File_source_station_saves_the_path()
    {
        var cut = RenderStations();

        ClickButton(cut, "保存");

        var saved = Assert.Single(Config.SavedStations);
        Assert.Equal("D:/data/result.json", saved.DataFilePath);
        Assert.Contains(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Missing_file_warns_but_does_not_block_saving()
    {
        // 文件由设备写、每件覆写：配置时它完全可以还没出现（也可能通过映射盘共享），
        // 所以这里只能提示，不能像 PLC 地址那样拦死。
        var cut = RenderStations(FileStation(filePath: "D:/data/never-written-yet.json"));
        OpenTagTab(cut);

        Assert.Contains("现在没有文件", cut.Markup);
        ClickButton(cut, "保存");
        Assert.Contains(nameof(IConfigRepository.SaveStationAsync), Config.Calls);
    }

    [Fact]
    public void Plc_source_station_has_no_file_path_error_and_keeps_the_plc_column()
    {
        var station = FileStation();
        station.DataFilePath = "";
        foreach (var tag in station.Tags)
        {
            tag.Source = TagDataSource.Plc;
            tag.Address = "D1100";
        }

        var cut = RenderStations(station);
        OpenTagTab(cut);

        // 回归：PLC 源点位照旧按 PLC 地址解释，也不受"没配数据文件"影响。
        Assert.Contains("PLC 寄存器", cut.Markup);
        Assert.Contains("单精度浮点", cut.Markup);
        ClickButton(cut, "新增点位");
        var (_, parameters) = Assert.Single(Dialogs.Shown);
        Assert.Equal("", parameters.Get<string>("DataFilePath"));
    }

    [Fact]
    public void Csv_format_uses_the_csv_filter_and_is_saved_with_the_station()
    {
        var station = FileStation();
        station.DataFileFormat = DataFileFormat.Csv;
        var cut = RenderStations(station);
        OpenTagTab(cut);
        FileDialog.Result = @"D:\device\result.csv";

        ClickByAriaLabel(cut, "选择数据文件");

        Assert.Equal(DataFileFormat.Csv, FileDialog.LastFormat);
        Assert.Contains(@"D:\device\result.csv", cut.Markup);
        Assert.Contains("取自 CSV 文件", cut.Markup);

        ClickButton(cut, "保存");
        var saved = Assert.Single(Config.SavedStations);
        Assert.Equal(DataFileFormat.Csv, saved.DataFileFormat);
        Assert.Equal(@"D:\device\result.csv", saved.DataFilePath);
    }
}
