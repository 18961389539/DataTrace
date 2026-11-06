namespace DataTrace.Application.Identity;

/// <summary>MCP 访问令牌的 scope。read 覆盖全部只读工具，write 为二期写入工具预留。</summary>
public static class McpScopes
{
    public const string Read = "read";
    public const string Write = "write";

    public static readonly IReadOnlyList<string> All = [Read, Write];

    public static bool IsKnown(string scope)
        => scope is Read or Write;
}

public sealed record McpTokenSummary(
    int Id,
    string Name,
    string UserName,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    DateTime? LastUsedAt)
{
    public bool IsActive(DateTime now) => RevokedAt is null && ExpiresAt > now;
}

public sealed record CreatedMcpToken(McpTokenSummary Summary, string Plaintext);

/// <summary>认证通过后交给鉴权链的最小身份：绑定用户 + 令牌 scope。</summary>
public sealed record McpTokenPrincipal(int TokenId, string UserName, IReadOnlyList<string> Scopes);

public interface IMcpTokenStore
{
    Task<IReadOnlyList<McpTokenSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<CreatedMcpToken> CreateAsync(
        string userName,
        string name,
        IReadOnlyList<string> scopes,
        int lifetimeDays,
        string actor,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(int id, string actor, CancellationToken cancellationToken = default);

    /// <summary>校验明文令牌；失败返回 null，不区分不存在、过期、撤销，避免信息泄漏。</summary>
    Task<McpTokenPrincipal?> ValidateAsync(string plaintext, CancellationToken cancellationToken = default);
}
