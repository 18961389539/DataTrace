using System.Reflection;

namespace DataTrace.Web.Services;

/// <summary>
/// 产物的构建标识：<c>1.1.0+d279acf</c> 里的"哪次提交"那一半。
/// </summary>
/// <remarks>
/// 值由构建时算好写进 <see cref="AssemblyInformationalVersionAttribute"/>（见 DataTrace.Web.csproj 的盖章说明），
/// 取值顺序是 GITHUB_SHA → 直接读 .git → nogit。
/// 现场问"这台机器跑的是哪次提交"时，设置页的「构建标识」与 deploy\verify.ps1 都读这里，
/// 不再只能答出人工 bump 的版本号。
/// </remarks>
public static class BuildInfo
{
    /// <summary>提交号（短）；构建环境取不到 git 信息时为 <c>nogit</c>，永远不为空。</summary>
    public static string Commit { get; } = Parse(InformationalVersion).Commit;

    /// <summary>不含构建标识的版本号，例如 1.1.0。</summary>
    public static string Version { get; } = Parse(InformationalVersion).Version;

    /// <summary>给界面看的构建标识；取不到提交号时说明原因，不显示空白。</summary>
    public static string CommitLabel { get; } = Commit == "nogit" ? "未记录（构建时取不到 git 信息）" : Commit;

    private static string InformationalVersion
        => typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString(3)
           ?? "";

    /// <summary>
    /// 拆开 <c>版本+提交</c>。没有 <c>+</c> 时提交号为空串（调用方各自决定怎么显示）。
    /// </summary>
    public static (string Version, string Commit) Parse(string? informationalVersion)
    {
        var text = (informationalVersion ?? "").Trim();
        if (text.Length == 0)
        {
            return ("0.0.0", "");
        }

        var plus = text.IndexOf('+');
        return plus > 0
            ? (text[..plus], text[(plus + 1)..])
            : (text, "");
    }
}