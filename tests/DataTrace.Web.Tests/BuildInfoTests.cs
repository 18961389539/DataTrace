using DataTrace.Web.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 构建标识：产物必须能回答"它是哪次提交编出来的"。
/// </summary>
/// <remarks>
/// 值由构建时烘焙进 InformationalVersion（见 DataTrace.Web.csproj 的盖章说明），
/// 现场靠设置页与 deploy\verify.ps1 读它；这里钉住拆分规则与"永远有值"这两条契约，
/// 免得某次构建方式一变，产物上悄悄只剩一个版本号。
/// </remarks>
public class BuildInfoTests : WebTestBase
{
    [Theory]
    [InlineData("1.1.0+d279acf", "1.1.0", "d279acf")]
    [InlineData("1.1.0", "1.1.0", "")]
    [InlineData("", "0.0.0", "")]
    [InlineData(null, "0.0.0", "")]
    public void Parse_splits_the_version_from_the_commit(string? informationalVersion, string expectedVersion, string expectedCommit)
    {
        var (version, commit) = BuildInfo.Parse(informationalVersion);

        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedCommit, commit);
    }

    /// <summary>
    /// 本产物必须带真实提交号：要么来自 CI 的 GITHUB_SHA，要么读自检出目录的 .git。
    /// </summary>
    /// <remarks>
    /// nogit 会在这里红：那说明这批产物无法与任何提交对应（例如用源码压缩包构建），
    /// 而"现场跑的是哪次提交"正是这个字段要回答的问题。要用压缩包构建时请设 GITHUB_SHA。
    /// </remarks>
    [Fact]
    public void The_shipped_assembly_carries_a_build_marker()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Commit), "产物没有构建标识：盖章没生效？");
        Assert.NotEqual("nogit", BuildInfo.Commit);
        Assert.Matches(@"^\d+\.\d+\.\d+$", BuildInfo.Version);
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.CommitLabel));
    }

    /// <summary>
    /// 客户看到的版本号是干净的（不含 + 提交号）；提交号单独一栏显示。
    /// </summary>
    [Fact]
    public void Branding_exposes_a_clean_version_and_the_commit_separately()
    {
        Assert.Equal(BuildInfo.Version, Branding.AppVersion);
        Assert.DoesNotContain("+", Branding.AppVersion);
        Assert.Equal(BuildInfo.Commit, Branding.BuildCommit);
    }
}