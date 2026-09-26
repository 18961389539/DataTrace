using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DataTrace.Web.Tests;

/// <summary>
/// 点位编辑对话框：两套来源下的校验分路，以及保存时交回给页面的那份点位。
/// </summary>
/// <remarks>
/// 走真对话框路径（真 DialogService + MudDialogProvider），否则测不到对话框自己渲染了哪些字段，
/// 也测不到"提交被拦下"是真的没有关闭窗口。
/// </remarks>
public class TagEditDialogTests : WebTestBase
{
    private static TagDefinition Tag() => new()
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
    };

    private static IReadOnlyList<TagDefinition> Siblings() =>
    [
        Tag(),
        new TagDefinition { Id = 32, StationId = 10, Name = "温度", Address = "data.temp" }
    ];

    /// <summary>打开真对话框：返回它的宿主（供断言渲染内容）与结果句柄（供断言是否已交回）。</summary>
    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> OpenAsync(
        TagDefinition tag,
        bool isNew = false,
        PlcBrand? brand = PlcBrand.MitsubishiMc3E,
        string dataFilePath = "D:/data/result.json")
    {
        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.RenderComponent<MudDialogProvider>();
        var dialogs = Context.Services.GetRequiredService<IDialogService>();
        var reference = await dialogs.ShowAsync<TagEditDialog>("点位", new DialogParameters
        {
            ["Tag"] = tag,
            ["IsNew"] = isNew,
            ["Brand"] = brand,
            ["DataFilePath"] = dataFilePath,
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

    [Fact]
    public async Task File_source_dialog_treats_the_address_as_a_json_field_path()
    {
        var tag = Tag();
        tag.Address = "force.peak";
        var (provider, reference) = await OpenAsync(tag);

        // force.peak 在 PLC 地址规则下是非法写法；文件源下它只是两层字段名。
        Assert.Contains("JSON 字段", provider.Markup);
        Assert.DoesNotContain("非法", provider.Markup);
        // 文件里没有字宽与字序这回事：类型只给数值/文本，长度、倍率、偏移不出现。
        Assert.Contains("数值", provider.Markup);
        Assert.DoesNotContain("倍率", provider.Markup);
        Assert.DoesNotContain("长度", provider.Markup);

        ClickDialogButton(provider, "保存并下发");

        var result = await reference.Result;
        var edited = Assert.IsType<TagDefinition>(result!.Data);
        Assert.Equal("force.peak", edited.Address);
    }

    [Fact]
    public async Task Array_index_in_the_field_path_blocks_saving()
    {
        var tag = Tag();
        tag.Address = "items[0].force";
        var (provider, reference) = await OpenAsync(tag);

        Assert.Contains("不支持数组下标", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Empty_field_path_blocks_saving()
    {
        var tag = Tag();
        tag.Address = "   ";
        var (provider, reference) = await OpenAsync(tag);

        Assert.Contains("JSON 字段不能为空", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task New_tag_dialog_opens_without_prefilled_errors()
    {
        // 新增时一进来不该满屏红字：报错要等用户提交或动过字段之后才出现。
        var (provider, _) = await OpenAsync(
            new TagDefinition { StationId = 10, DataType = PlcDataType.Float },
            isNew: true);

        Assert.DoesNotContain("不能为空", provider.Markup);
    }

    [Fact]
    public async Task Plc_source_dialog_validates_the_address_against_the_brand()
    {
        var tag = Tag();
        tag.Source = TagDataSource.Plc;
        tag.Address = "M100";
        var (provider, reference) = await OpenAsync(tag);

        // 位地址解析得出来但采集读不到：PLC 源下必须在保存前拦住。
        Assert.Contains("位地址暂不支持", provider.Markup);
        // PLC 源下才显示字宽与解码参数。
        Assert.Contains("倍率", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Plc_source_dialog_accepts_a_field_name_looking_address()
    {
        var tag = Tag();
        // 回归：PLC 源下合法地址照常通过，别被文件源那套规则顺手拦下。
        tag.Source = TagDataSource.Plc;
        tag.Address = "D1100";
        var (provider, reference) = await OpenAsync(tag);

        ClickDialogButton(provider, "保存并下发");

        var result = await reference.Result;
        Assert.Equal("D1100", Assert.IsType<TagDefinition>(result!.Data).Address);
    }

    /// <summary>
    /// 选了 JSON 来源但工站还没配数据文件路径：直接拦下并说清去哪儿补。
    /// </summary>
    /// <remarks>
    /// 放它落库的话，采集端每个周期都会因读不到文件而失败（结果码 9），
    /// 而现场从界面看不出问题在哪。
    /// </remarks>
    [Fact]
    public async Task File_source_without_a_configured_path_blocks_saving()
    {
        var tag = Tag();
        var (provider, reference) = await OpenAsync(tag, dataFilePath: "");

        Assert.Contains("还没配「数据文件路径」", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Plc_source_tag_saves_even_when_the_station_has_no_file_path()
    {
        var tag = Tag();
        tag.Source = TagDataSource.Plc;
        tag.Address = "D1100";
        // 整站都从 PLC 读时，数据文件路径是条用不上的配置，不该拦住任何一个点位。
        var (provider, reference) = await OpenAsync(tag, dataFilePath: "");

        ClickDialogButton(provider, "保存并下发");

        var result = await reference.Result;
        var edited = Assert.IsType<TagDefinition>(result!.Data);
        Assert.Equal(TagDataSource.Plc, edited.Source);
        Assert.Equal("D1100", edited.Address);
    }

    [Fact]
    public async Task Inconsistent_limits_block_saving()
    {
        var tag = Tag();
        // 黄线跑到红线外侧：采集端会按红线收敛，界面显示与实际判据就不是一回事了。
        tag.UpperLimit = 20;
        tag.WarningUpperLimit = 30;
        var (provider, reference) = await OpenAsync(tag);

        Assert.Contains("不能", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Duplicated_name_is_rejected()
    {
        var tag = Tag();
        tag.Name = "温度";
        var (provider, reference) = await OpenAsync(tag);

        Assert.Contains("已存在", provider.Markup);
        ClickDialogButton(provider, "保存并下发");
        AssertStillOpen(reference);
    }

    [Fact]
    public async Task Saving_trims_the_typed_values()
    {
        var tag = Tag();
        tag.Address = "  force  ";
        tag.Name = " 压力 ";
        var (provider, reference) = await OpenAsync(tag);

        ClickDialogButton(provider, "保存并下发");

        var result = await reference.Result;
        var edited = Assert.IsType<TagDefinition>(result!.Data);
        Assert.Equal("force", edited.Address);
        Assert.Equal("压力", edited.Name);
    }

    [Fact]
    public async Task Cancel_returns_no_result_so_the_page_writes_nothing()
    {
        var (provider, reference) = await OpenAsync(Tag());

        ClickDialogButton(provider, "取消");

        var result = await reference.Result;
        Assert.Null(result?.Data);
    }
}