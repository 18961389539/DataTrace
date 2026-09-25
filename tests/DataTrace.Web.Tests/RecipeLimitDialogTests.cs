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
            Code = code,
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
        var station = new Station
        {
            Id = 1,
            Code = "ST010",
            Name = "一号站",
            Sequence = 1,
            Tags = [temperature, pressure]
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

        return (recipe, [station]);
    }

    [Fact]
    public async Task Disabled_tag_is_listed_with_its_override_intact()
    {
        var (recipe, stations) = Fixtures();

        // 走真对话框路径（真 DialogService + MudDialogProvider），否则测不到对话框自己列了哪些点位。
        Context.Services.AddSingleton<IDialogService, DialogService>();
        var provider = Context.RenderComponent<MudDialogProvider>();
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
}
