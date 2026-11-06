namespace DataTrace.Infrastructure.Identity;

/// <summary>
/// MCP 访问令牌。只存哈希与前缀，明文只在创建时返回一次；绑定到 Identity 用户，
/// 角色变化、锁定或删除会立即影响令牌能否使用。
/// </summary>
public sealed class McpAccessToken
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Scopes { get; set; } = "read";
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
