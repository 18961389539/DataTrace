using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;

namespace DataTrace.Tests;

/// <summary>型号编码格式：逗号会打散"历史编码"口径，必须在落库前拦住。</summary>
public class RecipeCodeRulesTests
{
    [Theory]
    [InlineData("A100")]
    [InlineData("B-300")]
    [InlineData("M_2")]
    [InlineData("型号A")]
    public void Accepts_letters_digits_dash_and_underscore(string code)
        => Assert.Null(RecipeCodeRules.Error(code));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("P,Q")]
    [InlineData("A 100")]
    [InlineData("A#100")]
    public void Rejects_blank_and_separator_characters(string code)
        => Assert.NotNull(RecipeCodeRules.Error(code));

    [Fact]
    public void Comma_message_explains_why()
    {
        var error = RecipeCodeRules.Error("P,Q");
        Assert.NotNull(error);
        Assert.Contains("逗号", error);
    }
}

/// <summary>可覆盖点位名单：停用点位要留在矩阵里，否则"打开→保存"就把覆盖值删了。</summary>
public class RecipeLimitScopeTests
{
    private static Station StationWith(params TagDefinition[] tags)
        => new() { Id = 1, Code = "ST010", Name = "一号站", Tags = tags.ToList() };

    private static TagDefinition Tag(int id, string code, PlcDataType type, bool enabled = true)
        => new() { Id = id, StationId = 1, Name = code, DataType = type, Enabled = enabled };

    [Fact]
    public void Disabled_tags_still_count_as_overridable()
    {
        var station = StationWith(
            Tag(1, "ST010_TEMP", PlcDataType.Float, enabled: false),
            Tag(2, "ST010_P1", PlcDataType.Float));

        var tags = RecipeLimitScope.OverridableTags([station]).ToList();

        Assert.Equal(2, tags.Count);
        Assert.Contains(tags, t => t.Id == 1);
    }

    [Fact]
    public void String_and_bool_tags_are_not_overridable()
    {
        var station = StationWith(
            Tag(1, "ST010_TEXT", PlcDataType.String),
            Tag(2, "ST010_FLAG", PlcDataType.Bool),
            Tag(3, "ST010_P1", PlcDataType.Float));

        var tag = Assert.Single(RecipeLimitScope.OverridableTags([station]));
        Assert.Equal(3, tag.Id);
    }
}

/// <summary>历史编码归一：改过编码的型号，旧码必须与本型号算同一批样本。</summary>
public class CurveRecipeScopeTests
{
    private static Recipe Recipe(string code, string? previousCodes = null)
        => new() { Id = 1, Code = code, Name = code, PreviousCodes = previousCodes };

    [Fact]
    public void Finds_a_recipe_by_its_previous_code()
    {
        var recipe = Recipe("B300", "A100,B200");

        Assert.Equal("B300", CurveRecipeScope.FindByAnyCode([recipe], "A100")?.Code);
        Assert.Null(CurveRecipeScope.FindByAnyCode([recipe], "C400"));
        Assert.Null(CurveRecipeScope.FindByAnyCode([recipe], ""));
    }

    [Fact]
    public void Canonical_code_merges_previous_codes_into_the_current_one()
    {
        var recipes = new[] { Recipe("B300", "A100") };

        Assert.Equal("B300", CurveRecipeScope.CanonicalCode("A100", recipes));
        Assert.Equal("B300", CurveRecipeScope.CanonicalCode("B300", recipes));
        // 其它型号与"未选型号"原样返回：不认识的编码不该被并到任何型号里。
        Assert.Equal("C400", CurveRecipeScope.CanonicalCode("C400", recipes));
        Assert.Equal("", CurveRecipeScope.CanonicalCode("", recipes));
        Assert.Equal("", CurveRecipeScope.CanonicalCode(null, recipes));
    }

    [Fact]
    public void Commas_inside_a_code_do_not_split_the_scope()
    {
        // 编码本身带逗号曾把 PreviousCodes 拆成两个不存在的编码（P,Q → P / Q），
        // 于是别的型号的样本会被算进本型号。格式校验落库前就拒绝这种编码。
        Assert.NotNull(RecipeCodeRules.Error("P,Q"));
    }
}

/// <summary>型号仓储：限值同步、历史编码收权、编码/名称校验。</summary>
public class RecipeRepositoryTests
{
    [Fact]
    public async Task Deleting_a_station_also_removes_its_recipe_limits()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var repo = harness.ConfigRepository;

        var snapshot = await repo.GetSnapshotAsync();
        var station = snapshot.Stations
            .First(s => s.Tags.Any(t => t.Name == "工站温度"));
        var tagIds = station.Tags.Select(t => t.Id).ToHashSet();
        var before = snapshot.Recipes.Single(r => r.Code == "A100");
        Assert.Contains(before.Limits, l => tagIds.Contains(l.TagId));

        await repo.DeleteStationAsync(station.Id);

        var after = await repo.GetSnapshotAsync();
        var recipe = after.Recipes.Single(r => r.Code == "A100");
        var validTagIds = after.Stations.SelectMany(s => s.Tags).Select(t => t.Id).ToHashSet();
        // 悬空行会在限值矩阵里"隐身"，却继续被"覆盖点位 N 个"计数。
        Assert.DoesNotContain(recipe.Limits, l => !validTagIds.Contains(l.TagId));
        Assert.DoesNotContain(recipe.Limits, l => tagIds.Contains(l.TagId));
    }

    [Fact]
    public async Task Saving_limits_ignores_tags_that_do_not_exist()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var repo = harness.ConfigRepository;

        var recipe = (await repo.GetSnapshotAsync()).Recipes.Single(r => r.Code == "A100");
        var kept = recipe.Limits.First();

        await repo.SaveRecipeLimitsAsync(recipe.Id,
        [
            new RecipeLimit { TagId = kept.TagId, UpperLimit = kept.UpperLimit },
            new RecipeLimit { TagId = 999_999, UpperLimit = 1 }
        ]);

        var after = (await repo.GetSnapshotAsync()).Recipes.Single(r => r.Code == "A100");
        var row = Assert.Single(after.Limits);
        Assert.Equal(kept.TagId, row.TagId);
    }

    [Fact]
    public async Task Saving_limits_does_not_revive_a_recipe_disabled_elsewhere()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var repo = harness.ConfigRepository;

        var snapshot = await repo.GetSnapshotAsync();
        var recipe = snapshot.Recipes.Single(r => r.Code == "A100");
        await repo.SetActiveRecipeAsync(recipe.Id);

        // 另一个会话把这个型号停用：指针被清掉，快照（也就是限值对话框手上的那份）已经过期。
        snapshot = await repo.GetSnapshotAsync();
        var stale = snapshot.Recipes.Single(r => r.Code == "A100");
        stale.Enabled = false;
        stale.Remark = "另一会话改的备注";
        await repo.SaveRecipeAsync(stale);

        // 过期的那一份接着保存限值：不能把 Enabled/备注写回旧值。
        await repo.SaveRecipeLimitsAsync(recipe.Id, stale.Limits.ToList());

        var after = await repo.GetSnapshotAsync();
        var current = after.Recipes.Single(r => r.Code == "A100");
        Assert.False(current.Enabled);
        Assert.Equal("另一会话改的备注", current.Remark);
        Assert.Null(after.ActiveRecipe);
        Assert.Null(after.Settings.ActiveRecipeId);
    }

    [Fact]
    public async Task Rejects_blank_and_illegal_codes()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var repo = harness.ConfigRepository;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SaveRecipeAsync(new Recipe { Code = "   ", Name = "空编码" }));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SaveRecipeAsync(new Recipe { Code = "P,Q", Name = "逗号编码" }));
    }

    [Fact]
    public async Task Blank_name_falls_back_to_the_code()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var repo = harness.ConfigRepository;

        await repo.SaveRecipeAsync(new Recipe { Code = "C700", Name = "" });

        var saved = (await repo.GetSnapshotAsync()).Recipes.Single(r => r.Code == "C700");
        Assert.Equal("C700", saved.Name);
    }

    [Fact]
    public async Task Reusing_a_previous_code_takes_it_away_from_the_old_model()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var repo = harness.ConfigRepository;

        // A100 改名为 B300：A100 进入历史编码（曲线基线仍认它）。
        var renamed = (await repo.GetSnapshotAsync()).Recipes.Single(r => r.Code == "A100");
        renamed.Code = "B300";
        await repo.SaveRecipeAsync(renamed);
        Assert.Equal("A100", (await repo.GetSnapshotAsync()).Recipes.Single(r => r.Code == "B300").PreviousCodes);

        // 再新建一个 A100：一个编码在某一刻只能属于一个型号，B300 不能再认这批样本。
        await repo.SaveRecipeAsync(new Recipe { Code = "A100", Name = "重新启用的老编码" });

        var after = await repo.GetSnapshotAsync();
        var b300 = after.Recipes.Single(r => r.Code == "B300");
        Assert.DoesNotContain("A100", CurveRecipeScope.AllowedCodes(b300.Code, b300.PreviousCodes));
        Assert.NotNull(after.Recipes.Single(r => r.Code == "A100"));
    }
}
