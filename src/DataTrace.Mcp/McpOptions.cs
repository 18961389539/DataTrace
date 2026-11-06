namespace DataTrace.Mcp;

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public bool Enabled { get; set; }
    public bool RequireHttps { get; set; } = true;
    public bool AllowInsecureHttp { get; set; }
    public bool AllowWrites { get; set; }
    public int MaxRangeDays { get; set; } = 31;
    public int MaxPageSize { get; set; } = 100;
    public int MaxTrendPoints { get; set; } = 500;
    public int MaxSpcSamples { get; set; } = 2000;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int RateLimitPerMinute { get; set; } = 60;
    public int HeavyQueryConcurrency { get; set; } = 4;
}

public static class McpAuthentication
{
    public const string Scheme = "McpToken";
    public const string ScopeClaim = "scope";
    public const string TokenIdClaim = "token_id";
}

public static class McpPolicies
{
    public const string Read = "McpRead";
    public const string Engineer = "McpEngineer";
    public const string Admin = "McpAdmin";
    public const string Write = "McpWrite";
}
