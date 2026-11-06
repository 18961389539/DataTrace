using System.Reflection;
using DataTrace.Mcp;
using DataTrace.Mcp.Tools;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace DataTrace.Web.Tests;

public sealed class McpToolCatalogTests
{
    private static readonly Assembly[] ToolAssemblies =
    [
        typeof(StatusTools).Assembly,
        typeof(DataTrace.Web.Services.SystemHealthTools).Assembly
    ];

    [Fact]
    public void Every_read_tool_declares_an_authorization_policy_and_unique_name()
    {
        var tools = FindReadTools();
        Assert.Equal(18, tools.Count);

        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        foreach (var tool in tools)
        {
            var authorize = tool.Method.GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(authorize);
            Assert.False(string.IsNullOrWhiteSpace(authorize!.Policy), $"{tool.Name} 缺少授权策略");
        }
    }

    [Fact]
    public void Resources_and_prompts_are_discoverable_and_authorized()
    {
        var resources = typeof(DataTrace.Mcp.Resources.CatalogResources).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerResourceTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerResourceAttribute>()))
            .Where(x => x.Attribute is not null)
            .ToList();
        Assert.Equal(3, resources.Count);
        Assert.Equal(3, resources.Select(r => r.Attribute!.UriTemplate).Distinct().Count());
        Assert.All(resources, r =>
            Assert.Equal(McpPolicies.Read, r.Method.GetCustomAttribute<AuthorizeAttribute>()?.Policy));

        var prompts = typeof(DataTrace.Mcp.Prompts.DataTracePrompts).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerPromptTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerPromptAttribute>()))
            .Where(x => x.Attribute is not null)
            .ToList();
        Assert.Equal(3, prompts.Count);
        Assert.All(prompts, p =>
            Assert.Equal(McpPolicies.Read, p.Method.GetCustomAttribute<AuthorizeAttribute>()?.Policy));
    }

    [Fact]
    public void Write_tools_are_not_part_of_the_default_catalog_and_require_write_policy()
    {
        Assert.DoesNotContain(FindReadTools(), t => t.Name == "acknowledge_alarms");
        Assert.Null(typeof(WriteTools).GetCustomAttribute<McpServerToolTypeAttribute>());

        var writes = typeof(WriteTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attribute is not null)
            .ToList();
        Assert.Equal(3, writes.Count);
        foreach (var write in writes)
        {
            Assert.Equal(McpPolicies.Write, write.Method.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        }
    }

    [Fact]
    public void Sensitive_tools_require_engineer_or_admin()
    {
        Assert.Equal(McpPolicies.Engineer, PolicyOf("get_operational_config"));
        Assert.Equal(McpPolicies.Engineer, PolicyOf("get_mes_outbox_status"));
        Assert.Equal(McpPolicies.Admin, PolicyOf("get_audit_logs"));
        Assert.Equal(McpPolicies.Read, PolicyOf("query_records"));
        Assert.Equal(McpPolicies.Read, PolicyOf("get_collection_diagnostics"));
        Assert.Equal(McpPolicies.Read, PolicyOf("system_health"));
    }

    [Fact]
    public void Read_tools_are_declared_read_only()
    {
        foreach (var tool in FindReadTools())
        {
            Assert.True(tool.Attribute.ReadOnly, $"{tool.Name} 应标记 ReadOnly");
            var description = tool.Method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            Assert.False(string.IsNullOrWhiteSpace(description?.Description), $"{tool.Name} 缺少说明");
        }
    }

    private static string? PolicyOf(string name)
        => FindReadTools().Single(t => t.Name == name).Method
            .GetCustomAttribute<AuthorizeAttribute>()?.Policy;

    private static List<(string Name, MethodInfo Method, McpServerToolAttribute Attribute)> FindReadTools()
        => ToolAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Attribute!.Name ?? x.Method.Name, x.Method, x.Attribute))
            .ToList();
}
