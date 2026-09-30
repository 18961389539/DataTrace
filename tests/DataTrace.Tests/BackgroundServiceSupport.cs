using System.Net.Http;
using DataTrace.Application.Realtime;
using DataTrace.Collector;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataTrace.Tests;

/// <summary>把日志留在内存里，好让"后台服务没动作"的超时报错能带上真实原因。</summary>
internal sealed class CollectingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message, Exception? Error)> Entries { get; } = [];

    /// <summary>
    /// 带作用域事实的日志行：键值对就是 <c>BeginScope</c> 传进来的那些。
    /// </summary>
    /// <remarks>
    /// 用它与"只写进消息文本"区分开 —— 采集链路关联靠的是作用域，
    /// 只断言消息里有流水号，测不出作用域到底有没有生效。
    /// </remarks>
    public List<(string Message, IReadOnlyDictionary<string, object> Facts)> Scoped { get; } = [];

    /// <summary>
    /// 当前异步流上的作用域栈。
    /// </summary>
    /// <remarks>
    /// 放在 provider 上而不是每个 logger 上：作用域要跨类别生效
    /// （流水线开的作用域必须能被协调器记的日志看见），而真实 <c>ILogger</c> 也是这么做的。
    /// 用 <see cref="AsyncLocal{T}"/> 是为了和真实实现一致地按异步流隔离：
    /// 各工站的采集任务并发跑，作用域不能互相串味。
    /// </remarks>
    internal AsyncLocal<List<IReadOnlyDictionary<string, object>>?> Ambient { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CollectingLogger(this, categoryName);

    public void Dispose()
    {
    }

    public string Summarize()
    {
        lock (Entries)
        {
            return string.Join(
                " | ",
                Entries
                    .Where(x => x.Level >= LogLevel.Warning)
                    .Select(x => $"{x.Category}:{x.Message}{(x.Error is null ? "" : " -> " + x.Error.Message)}"));
        }
    }

    /// <summary>日志是在后台线程写的，直接遍历 <see cref="Entries"/> 会撞上"集合已被修改"，一律走这个快照。</summary>
    public List<(LogLevel Level, string Category, string Message, Exception? Error)> Snapshot()
    {
        lock (Entries)
        {
            return [.. Entries];
        }
    }

    /// <summary>全部日志（含 Information/Debug）的紧凑转储，用于超时报错时给出完整上下文。</summary>
    public string Dump()
    {
        lock (Entries)
        {
            return string.Join(
                " | ",
                Entries
                    .Where(x => x.Category.StartsWith("DataTrace", StringComparison.Ordinal)
                                || x.Level >= LogLevel.Warning)
                    .Select(x => $"{x.Level}:{x.Category}:{x.Message}{(x.Error is null ? "" : " -> " + x.Error.Message)}")
                    .TakeLast(40));
        }
    }

    private sealed class CollectingLogger(CollectingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is not IEnumerable<KeyValuePair<string, object>> pairs)
            {
                return null;
            }

            var facts = pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
            var stack = owner.Ambient.Value ??= [];
            stack.Add(facts);
            return new ScopeHandle(stack, facts);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (owner.Entries)
            {
                owner.Entries.Add((logLevel, category, message, exception));
            }

            var stack = owner.Ambient.Value;
            if (stack is not { Count: > 0 })
            {
                return;
            }

            // 外层的工站/触发时刻 + 内层的流水号合成一份事实，跟日志行一起记下来。
            var merged = new Dictionary<string, object>();
            foreach (var frame in stack)
            {
                foreach (var pair in frame)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            lock (owner.Scoped)
            {
                owner.Scoped.Add((message, merged));
            }
        }

        /// <summary>只弹自己那一层：嵌套作用域由内向外释放，弹错会把外层的事实一起丢掉。</summary>
        private sealed class ScopeHandle(
            List<IReadOnlyDictionary<string, object>> stack,
            IReadOnlyDictionary<string, object> facts) : IDisposable
        {
            public void Dispose()
            {
                if (stack.Count > 0 && ReferenceEquals(stack[^1], facts))
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }
    }
}

/// <summary>
/// 只跑后台服务的第一轮就停机。
/// </summary>
/// <remarks>
/// 这些服务的 <c>ExecuteAsync</c> 把 <c>Task.Delay</c> 写在 try 之外，取消时会抛
/// <c>OperationCanceledException</c>；直接 await 会把"正常停机"误判成失败。
/// 所以走 <see cref="IHostedService"/> 的真实出入口，并靠捕获的日志解释超时。
/// </remarks>
internal static class HostedServiceProbe
{
    public static async Task RunUntilAsync(
        BackgroundService service,
        Func<bool> condition,
        Func<string> diagnostics,
        int timeoutMs = 10000)
    {
        using var cts = new CancellationTokenSource();
        var startedAt = DateTime.UtcNow;

        await ((IHostedService)service).StartAsync(cts.Token);
        try
        {
            while (true)
            {
                if (condition())
                {
                    return;
                }

                if ((DateTime.UtcNow - startedAt).TotalMilliseconds > timeoutMs)
                {
                    var detail = diagnostics();
                    throw new TimeoutException(
                        $"{service.GetType().Name} 在 {timeoutMs} ms 内未满足停止条件" +
                        (string.IsNullOrEmpty(detail) ? "" : $"；服务日志：{detail}"));
                }

                await Task.Delay(20);
            }
        }
        finally
        {
            cts.Cancel();
            await ((IHostedService)service).StopAsync(CancellationToken.None);
        }
    }
}

/// <summary>把出站 HTTP 换成内存处理器，避免测试依赖真实 MES 端点。</summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    public FakeHttpClientFactory(FakeHttpMessageHandler handler) => Handler = handler;

    public FakeHttpMessageHandler Handler { get; }

    public List<string> RequestedNames { get; } = [];

    public List<HttpClient> Clients { get; } = [];

    public HttpClient CreateClient(string name)
    {
        lock (RequestedNames)
        {
            RequestedNames.Add(name);
            // disposeHandler: false —— 处理器由工厂持有，服务每轮新建的 client 不该把它一起释放。
            var client = new HttpClient(Handler, disposeHandler: false);
            Clients.Add(client);
            return client;
        }
    }
}

/// <summary>可编程的 HTTP 处理器：记录出站请求，按脚本返回状态码。</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<int> _statusProvider;

    public FakeHttpMessageHandler(int status) : this(() => status)
    {
    }

    public FakeHttpMessageHandler(Func<int> statusProvider) => _statusProvider = statusProvider;

    public List<(HttpMethod Method, Uri? Uri, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri, body));
        }

        return new HttpResponseMessage((System.Net.HttpStatusCode)_statusProvider());
    }
}

/// <summary>只装配基础设施的临时容器，够后台服务用，不含 PLC 与采集流水线。</summary>
internal sealed class InfrastructureContext : IAsyncDisposable
{
    private InfrastructureContext(
        TempWorkspace workspace,
        ServiceProvider provider,
        IServiceScope scope,
        CollectingLoggerProvider logs)
    {
        Workspace = workspace;
        Provider = provider;
        Scope = scope;
        Logs = logs;
    }

    public TempWorkspace Workspace { get; }

    public ServiceProvider Provider { get; }

    public IServiceScope Scope { get; }

    public CollectingLoggerProvider Logs { get; }

    public static async Task<InfrastructureContext> CreateAsync(
        bool seed = true,
        Action<IServiceCollection>? configure = null)
    {
        var workspace = new TempWorkspace();
        var logs = new CollectingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddDataTraceInfrastructure(workspace.Root);
        services.AddScoped<CurveBaselineFactory>();
        // 采集侧运行时观测：与 AddDataTraceCollector 里的注册保持一致，
        // 否则直接构造采集服务的用例会解析不到它。
        services.AddSingleton<ICollectorDiagnostics, CollectorDiagnostics>();
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();

        if (seed)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        return new InfrastructureContext(workspace, provider, provider.CreateScope(), logs);
    }

    public IServiceScopeFactory ScopeFactory => Provider.GetRequiredService<IServiceScopeFactory>();

    public ILogger<T> Logger<T>()
    {
        var factory = Provider.GetRequiredService<ILoggerFactory>();
        return factory.CreateLogger<T>();
    }

    /// <summary>跑一轮后台服务直到条件成立。</summary>
    public Task RunAsync(BackgroundService service, Func<bool> condition, int timeoutMs = 10000)
        => HostedServiceProbe.RunUntilAsync(service, condition, Logs.Summarize, timeoutMs);

    /// <summary>
    /// 绕过仓储直接改设置行：模拟历史脏数据或脚本直写。
    /// 越界取值（保留年数 0、MES 超时 0）现在会被仓储层拒绝，消费端的兜底仍要扛得住这类存量数据。
    /// </summary>
    public async Task ForceSettingsAsync(Action<DataTrace.Domain.Entities.SystemSettings> mutate)
    {
        var db = Scope.ServiceProvider.GetRequiredService<DataTrace.Infrastructure.Persistence.ConfigDbContext>();
        var settings = await db.SystemSettings.FirstAsync();
        mutate(settings);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Scope.Dispose();
        await Provider.DisposeAsync();
        Workspace.Dispose();
    }
}
