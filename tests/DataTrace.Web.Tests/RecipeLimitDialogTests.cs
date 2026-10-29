using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Web.Tests;

/// <summary>
/// 型号限值对话框：停用点位必须留在矩阵里。
/// </summary>
/// <remarks>
/// 曾经按"启用中"过滤点位，于是打开对话框什么都不改、点保存，就会把停用点位的覆盖值删掉
/// （提交集合里没有它 = 最终状态里没有它 = 仓储把这一行删掉）。用户看到的是"限值自己没了"。
/// </remarks>
public class RecipeLimitDialogTests : WebTestBase
{
    private static TagDefinition Tag(int id, string code, bool enabled)
        => new()
        {
            Id = id,
            StationId = 1,
            Name = $"{code} 名称",
            DataType = PlcDataType.Float,
            LowerLimit = 5,
            UpperLimit = 20,
            Enabled = enabled
        };

    private static (Recipe Recipe, IReadOnlyList<Station> Stations) Fixtures()
    {
        var temperature = Tag(1, "ST010_TEMP", enabled: false);
        var pressure = Tag(2, "ST010_P1", enabled: true);
        pressure.Unit = "MPa";
        var flow = Tag(3, "ST020_FLOW", enabled: true);
        temperature.UpperLimit = 60;
        var station = new Station
        {
            Id = 1,
            Code = "ST010",
            Name = "一号站",
            Sequence = 1,
            Tags = [temperature, pressure]
        };
        var secondStation = new Station
        {
            Id = 2,
            Code = "ST020",
            Name = "二号站",
            Sequence = 2,
            Tags = [flow]
        };
        var recipe = new Recipe
        {
            Id = 7,
            Code = "A100",
            Name = "演示型号",
            Limits =
            [
                new RecipeLimit { Id = 11, RecipeId = 7, TagId = temperature.Id, WarningUpperLimit = 45 }
            ]
        };

        return (recipe, [station, secondStation]);
    }

    [Fact]
    public async Task Disabled_tag_is_listed_with_its_override_intact()
    {
        var (recipe, stations) = Fixtures();

        // 走真对话框路径（真 DialogService + MudDialogProvider），否则测不到对话框自己列了哪些点位。
        Context.Services.AddSingleton<IDialogService, DialogService>();
        // 对话框里现在有 ⓘ（MudTooltip）——浮层宿主由基类统一补。
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        var dialogs = Context.Services.GetRequiredService<IDialogService>();
        await dialogs.ShowAsync<RecipeLimitDialog>("型号限值", new DialogParameters
        {
            ["Recipe"] = recipe,
            ["Stations"] = stations
        });
        provider.Render();

        // 停用点位在矩阵里，且写明"已停用"——否则用户既看不见它、也不知道自己的覆盖值还在不在。
        var rows = provider.FindAll("tr");
        var row = rows.Single(r => r.TextContent.Contains("ST010_TEMP"));
        Assert.Contains("已停用", row.TextContent);

        // 覆盖值原样带出来：保存时它会被一并提交，而不是被当成"未覆盖"删掉。
        var warningUpper = row.QuerySelectorAll("input")[3];
        Assert.Equal("45", warningUpper.GetAttribute("value"));

        // 启用中的点位照旧在，且没有"已停用"字样。
        var enabledRow = rows.Single(r => r.TextContent.Contains("ST010_P1"));
        Assert.DoesNotContain("已停用", enabledRow.TextContent);
    }

    [Fact]
    public async Task Effective_preview_merges_override_fields_with_tag_defaults()
    {
        var (recipe, stations) = Fixtures();

        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        await Context.Services.GetRequiredService<IDialogService>().ShowAsync<RecipeLimitDialog>("型号限值", new DialogParameters
        {
            ["Recipe"] = recipe,
            ["Stations"] = stations
        });
        provider.Render();

        var row = provider.FindAll("tr").Single(r => r.TextContent.Contains("ST010_TEMP"));
        Assert.Contains("生效值预览", provider.Markup);
        Assert.Contains("规格 5 ~ 60", row.TextContent);
        Assert.Contains("预警 — ~ 45", row.TextContent);
    }

    [Fact]
    public async Task Limits_carry_the_point_unit_so_values_cannot_be_entered_in_the_wrong_scale()
    {
        var (recipe, stations) = Fixtures();

        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        await Context.Services.GetRequiredService<IDialogService>().ShowAsync<RecipeLimitDialog>("型号限值", new DialogParameters
        {
            ["Recipe"] = recipe,
            ["Stations"] = stations
        });
        provider.Render();

        var row = provider.FindAll("tr").Single(r => r.TextContent.Contains("ST010_P1"));
        Assert.Contains("（MPa）", row.TextContent);
        Assert.Contains("5 ~ 20 MPa", row.TextContent);
        Assert.Contains(provider.FindAll("input[aria-label]").Select(input => input.GetAttribute("aria-label")),
            name => name!.Contains("单位 MPa"));
    }

    [Fact]
    public async Task Numeric_inputs_have_accessible_names_with_station_tag_and_field()
    {
        var (recipe, stations) = Fixtures();

        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        await Context.Services.GetRequiredService<IDialogService>().ShowAsync<RecipeLimitDialog>("型号限值", new DialogParameters
        {
            ["Recipe"] = recipe,
            ["Stations"] = stations
        });
        provider.Render();

        var expectedNames = new[]
        {
            "ST010 ST010_TEMP 名称 规格下限",
            "ST010 ST010_TEMP 名称 规格上限",
            "ST010 ST010_TEMP 名称 预警下限",
            "ST010 ST010_TEMP 名称 预警上限",
            "ST010 ST010_TEMP 名称 目标值"
        };
        var inputs = provider.FindAll("input[aria-label]");

        Assert.All(expectedNames, name => Assert.Contains(inputs, input => input.GetAttribute("aria-label") == name));
        Assert.Equal(5, provider.FindAll("tr").Single(row => row.TextContent.Contains("ST010_TEMP"))
            .QuerySelectorAll("input[aria-label]").Length);
    }

    [Fact]
    public async Task Search_and_overridden_filter_only_change_visible_rows_not_saved_rows()
    {
        var (recipe, stations) = Fixtures();

        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        var reference = await Context.Services.GetRequiredService<IDialogService>().ShowAsync<RecipeLimitDialog>(
            "型号限值", new DialogParameters
            {
                ["Recipe"] = recipe,
                ["Stations"] = stations
            });
        provider.Render();

        var search = provider.Find("input[aria-label='按工站或点位搜索']");
        search.Input("ST020");
        Assert.Contains(provider.FindAll("tr"), row => row.TextContent.Contains("ST020_FLOW"));
        Assert.DoesNotContain(provider.FindAll("tr"), row => row.TextContent.Contains("ST010_TEMP"));

        search.Input("ST010_P1 名称");
        Assert.Contains(provider.FindAll("tr"), row => row.TextContent.Contains("ST010_P1"));
        Assert.DoesNotContain(provider.FindAll("tr"), row => row.TextContent.Contains("ST020_FLOW"));

        search.Input("ST020");
        var flowLower = provider.FindComponents<MudNumericField<double?>>()[0];
        await provider.InvokeAsync(() => flowLower.Instance.ValueChanged.InvokeAsync(9d));
        Assert.Contains("已编辑 1", provider.Markup);
        provider.Find("input[type=checkbox]").Change(true);
        Assert.Contains(provider.FindAll("tr"), row => row.TextContent.Contains("ST020_FLOW"));
        Assert.DoesNotContain(provider.FindAll("tr"), row => row.TextContent.Contains("ST010_TEMP"));
        search.Input("");
        Assert.Contains(provider.FindAll("tr"), row => row.TextContent.Contains("ST010_TEMP"));
        Assert.Contains(provider.FindAll("tr"), row => row.TextContent.Contains("ST020_FLOW"));
        Assert.DoesNotContain(provider.FindAll("tr"), row => row.TextContent.Contains("ST010_P1"));

        provider.FindAll("button").Single(button => button.TextContent.Contains("保存")).Click();
        var result = await reference.Result;
        var saved = Assert.IsType<List<RecipeLimit>>(result!.Data);
        Assert.Equal(2, saved.Count);
        Assert.Equal(45, saved.Single(limit => limit.TagId == 1).WarningUpperLimit);
        Assert.Equal(9, saved.Single(limit => limit.TagId == 3).LowerLimit);
    }
    [Fact]
    public async Task Toolbar_tip_and_clear_override_are_available()
    {
        var (recipe, stations) = Fixtures();

        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        await Context.Services.GetRequiredService<IDialogService>().ShowAsync<RecipeLimitDialog>("型号限值", new DialogParameters
        {
            ["Recipe"] = recipe,
            ["Stations"] = stations
        });
        provider.Render();

        Assert.Contains("dt-recipe-limit-toolbar", provider.Markup);
        Assert.Contains("dt-recipe-limit-tip", provider.Markup);
        Assert.Contains("限值说明（目标值不参与判定；空白项沿用点位默认）", provider.Markup);

        var overridden = provider.FindAll("tr").Single(r => r.TextContent.Contains("ST010_TEMP"));
        Assert.Contains("已覆盖", overridden.TextContent);
        overridden.QuerySelectorAll("button").Single(b => b.TextContent.Contains("清除覆盖")).Click();
        provider.Render();
        Assert.Contains("已编辑 1", provider.Markup);
        Assert.DoesNotContain("已覆盖", provider.FindAll("tr").Single(r => r.TextContent.Contains("ST010_TEMP")).TextContent);
    }

    [Fact]
    public async Task Cancel_with_dirty_rows_asks_before_closing()
    {
        var (recipe, stations) = Fixtures();

        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        var reference = await Context.Services.GetRequiredService<IDialogService>().ShowAsync<RecipeLimitDialog>(
            "型号限值", new DialogParameters
            {
                ["Recipe"] = recipe,
                ["Stations"] = stations
            });
        provider.Render();

        var search = provider.Find("input[aria-label='按工站或点位搜索']");
        search.Input("ST020");
        var flowLower = provider.FindComponents<MudNumericField<double?>>()[0];
        await provider.InvokeAsync(() => flowLower.Instance.ValueChanged.InvokeAsync(9d));
        Assert.Contains("已编辑 1", provider.Markup);

        provider.FindAll("button").Single(button => button.TextContent.Trim() == "取消").Click();
        provider.Render();
        Assert.Contains("未保存的改动", provider.Markup);
        Assert.Contains("放弃并关闭", provider.Markup);

        provider.FindAll("button").Single(button => button.TextContent.Contains("继续编辑")).Click();
        provider.Render();
        Assert.Contains("ST020_FLOW", provider.Markup);
        Assert.False(reference.Result.IsCompleted); // 继续编辑后限值对话框仍未关闭
    }

    [Fact]
    public async Task Save_with_inconsistent_limits_marks_error_and_offers_jump()
    {
        var (recipe, stations) = Fixtures();

        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        var reference = await Context.Services.GetRequiredService<IDialogService>().ShowAsync<RecipeLimitDialog>(
            "型号限值", new DialogParameters
            {
                ["Recipe"] = recipe,
                ["Stations"] = stations
            });
        provider.Render();

        var search = provider.Find("input[aria-label='按工站或点位搜索']");
        search.Input("ST020");
        var fields = provider.FindComponents<MudNumericField<double?>>();
        await provider.InvokeAsync(() => fields[0].Instance.ValueChanged.InvokeAsync(20d));
        await provider.InvokeAsync(() => fields[1].Instance.ValueChanged.InvokeAsync(10d));
        provider.FindAll("button").Single(button => button.TextContent.Contains("保存")).Click();
        provider.Render();

        Assert.Contains("限值互相矛盾", provider.Markup);
        Assert.Contains("跳到第一处", provider.Markup);
        Assert.Contains("dt-row-error", provider.Markup);
        Assert.False(reference.Result.IsCompleted);
    }

}
