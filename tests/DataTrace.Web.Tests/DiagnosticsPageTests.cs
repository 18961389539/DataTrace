using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Web.Components.Pages;
using DataTrace.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Web.Tests;

/// <summary>
/// 运行诊断页：三块内容各自的空态与有数据态，以及丢弃补传缓存前的二次确认。
/// </summary>
/// <remarks>
/// 这一页是拉模式（不订阅推送），所以测试关注的是"读到什么就画什么"，
/// 以及几条刻意做出来的边界：空队列、超量队列、取消删除。
/// </remarks>
public class DiagnosticsPageTests : WebTestBase
{
    private readonly FakeHealthProbe _health = new();

    /// <summary>补传写入替身：整队补传的进度与取消用例要能挂住某一条。</summary>
    private readonly RecordingWriter Writer = new();

    [Fact]
    public void Empty_state_says_so_instead_of_showing_zeroes()
    {
        var (cut, _, _) = Render(new FakeSpool());

        Assert.Contains("还没有采集循环记录", cut.Markup);
        Assert.Contains("还没有 PLC 通信记录", cut.Markup);
        Assert.Contains("补传队列是空的", cut.Markup);
    }

    [Fact]
    public void Renders_loop_traffic_and_backlog()
    {
        var spool = new FakeSpool();
        spool.Entries.Add(Entry("stuck.spool.json", "P900", attempts: 3, error: "database is locked"));

        // 观测数据必须在渲染之前登记：页面是拉模式，只在初始化与自己的定时跳里读，
        // 渲染完再往里塞不会触发重读（那正是"拉"而不是"推"的意思）。
        var (cut, _, _) = Render(spool, seed: diag =>
        {
            diag.PublishLoopTick(new CollectorLoopTick(
                new DateTime(2026, 9, 30, 10, 0, 0), ConfiguredIntervalMs: 50,
                WorkMs: 180, ScanMs: 120, HeartbeatMs: 8,
                PlcCount: 1, TriggeredCount: 2, CoolingDownPlcCount: 1, SkippedStationCount: 3));
            diag.PublishPlcTraffic(new PlcTrafficView(
                1, "1号PLC", TotalCount: 42, FailureCount: 3, LastDurationMs: 12, MaxDurationMs: 6000,
                LastSuccessAt: new DateTime(2026, 9, 30, 10, 0, 1),
                [
                    new PlcExchangeView(
                        new DateTime(2026, 9, 30, 10, 0, 1), IsWrite: false, "D100", 1, 12, true, null, [2])
                ],
                []));
        });

        Assert.Contains("平均 180 ms", cut.Markup);
        Assert.Contains("超时 1 轮", cut.Markup);
        Assert.Contains("1号PLC", cut.Markup);
        Assert.Contains("失败 3", cut.Markup);
        // 断线判读要靠"上次成功是多久以前"，光有累计失败数看不出断了多久。
        Assert.Contains("上次成功", cut.Markup);
        Assert.Contains("P900", cut.Markup);
        Assert.Contains("database is locked", cut.Markup);
        // 等待时长要与报警门槛并排：现场不用自己拿"最早一笔"去减当前时间。
        Assert.Contains("已等待", cut.Markup);
        Assert.Contains($"超过 {SystemDefaults.SpoolBacklogWarnMinutes} 分钟就会进报警页", cut.Markup);
    }

    /// <summary>
    /// "复制诊断摘要"要把三块内容与关键事实带出去：现场排障的最后一步是把它贴进工单/群。
    /// </summary>
    [Fact]
    public async Task Copy_digest_carries_the_key_facts_to_the_clipboard()
    {
        var spool = new FakeSpool();
        spool.Entries.Add(Entry("stuck.spool.json", "P900", attempts: 3, error: "database is locked"));

        var (cut, _, _) = Render(spool, seed: diag =>
        {
            diag.PublishLoopTick(new CollectorLoopTick(
                DateTime.Now, ConfiguredIntervalMs: 50,
                WorkMs: 180, ScanMs: 120, HeartbeatMs: 8,
                PlcCount: 1, TriggeredCount: 2, CoolingDownPlcCount: 0, SkippedStationCount: 0));
            diag.PublishPlcTraffic(new PlcTrafficView(
                1, "1号PLC", TotalCount: 42, FailureCount: 1, LastDurationMs: 12, MaxDurationMs: 6000,
                LastSuccessAt: DateTime.Now.AddSeconds(-5),
                [],
                [new PlcExchangeView(
                    DateTime.Now.AddSeconds(-2), false, "D100", 1, 40, false, "链接已断开", [])]));
        });

        var copy = Context.JSInterop.Setup<bool>("dtCopy", _ => true).SetResult(true);
        await cut.FindAll("button").Single(button => button.TextContent.Contains("复制诊断摘要"))
            .ClickAsync(new());

        var text = Assert.IsType<string>(Assert.Single(copy.Invocations).Arguments[0]);
        Assert.Contains("DataTrace 运行诊断摘要", text);
        Assert.Contains("版本 1.0.0", text);
        Assert.Contains("采集循环", text);
        Assert.Contains("PLC 1号PLC", text);
        Assert.Contains("链接已断开", text);
        Assert.Contains("database is locked", text);
    }

    /// <summary>
    /// 摘要把页面自身的状态也带上：暂停时读数只是快照。
    /// </summary>
    /// <remarks>
    /// 只写"生成时间：现在"而不标快照，拿到摘要的人会把它当成最新数据。
    /// </remarks>
    [Fact]
    public async Task Copy_digest_marks_the_snapshot_when_paused()
    {
        var spool = new FakeSpool();
        var (cut, _, _) = Render(spool);

        await cut.FindAll("button").Single(button => button.TextContent.Contains("暂停刷新")).ClickAsync(new());

        var copy = Context.JSInterop.Setup<bool>("dtCopy", _ => true).SetResult(true);
        await cut.FindAll("button").Single(button => button.TextContent.Contains("复制诊断摘要")).ClickAsync(new());

        var text = Assert.IsType<string>(Assert.Single(copy.Invocations).Arguments[0]);
        Assert.Contains("页面状态：已暂停", text);
        Assert.Contains("快照", text);
    }

    /// <summary>
    /// 页面读不到数时，摘要里也要把这件事写出来 —— 它恰恰是最该随读数一起带出去的。
    /// </summary>
    [Fact]
    public async Task Copy_digest_reports_a_failed_read()
    {
        var spool = new FakeSpool { FailNextDescribe = true };
        var (cut, _, _) = Render(spool);
        Assert.Contains("诊断数据读取失败", cut.Markup);

        var copy = Context.JSInterop.Setup<bool>("dtCopy", _ => true).SetResult(true);
        await cut.FindAll("button").Single(button => button.TextContent.Contains("复制诊断摘要")).ClickAsync(new());

        var text = Assert.IsType<string>(Assert.Single(copy.Invocations).Arguments[0]);
        Assert.Contains("读取状态：诊断数据读取失败", text);
    }

    /// <summary>
    /// 发过请求却一次都没成功：标题上要直接写"从未成功"，不能只给一个红着的失败数。
    /// </summary>
    [Fact]
    public void Plc_that_never_succeeded_is_called_out()
    {
        var failure = new PlcExchangeView(
            new DateTime(2026, 9, 30, 10, 0, 1), IsWrite: false, "D100", 1, 40, false, "链接已断开", []);

        var (cut, _, _) = Render(new FakeSpool(), seed: diag =>
            diag.PublishPlcTraffic(new PlcTrafficView(
                1, "1号PLC", TotalCount: 5, FailureCount: 5, LastDurationMs: 40, MaxDurationMs: 40,
                LastSuccessAt: null, [failure], [failure])));

        Assert.Contains("从未成功", cut.Markup);
    }

    /// <summary>
    /// 循环停掉时，页面必须自己说出来。
    /// </summary>
    /// <remarks>
    /// 这是这一页最大的盲区：Ticks 停止更新后，平均值/最长/超时数全都停在最后一轮的样子，
    /// 看起来比"一切正常"还像正常。所以"最后扫描距今"和随之而来的提示必须存在。
    /// </remarks>
    [Fact]
    public void Stalled_loop_is_called_out_instead_of_looking_healthy()
    {
        var (cut, _, _) = Render(new FakeSpool(), seed: SeedStalledTick);

        Assert.Contains("最后扫描", cut.Markup);
        Assert.Contains("采集循环已停止推进", cut.Markup);
    }

    /// <summary>
    /// 循环停了、但探活的服务心跳还新鲜：页面要把"服务在跑、停的是扫描"说出来。
    /// </summary>
    /// <remarks>
    /// 两条判据的口径差着一个量级（心跳 60 秒、循环约 5 倍扫描间隔），不解释的话，
    /// "全部正常"与"已停止推进"同屏出现就像是页面前后矛盾。
    /// </remarks>
    [Fact]
    public void Stalled_loop_with_fresh_service_heartbeat_points_at_the_scan_itself()
    {
        _health.Report = new HealthReport(
            "healthy", "1.0.0", "test", "Test", 1,
            [
                new HealthCheck("config-db", true, "ok"),
                new HealthCheck("collector", true, "ok")
            ]);

        var (cut, _, _) = Render(new FakeSpool(), seed: SeedStalledTick);

        // 探活项的名字自己也要说清判的是"服务"，不是"扫描"。
        Assert.Contains("采集服务心跳", cut.Markup);
        Assert.Contains("停的是扫描本身", cut.Markup);
    }

    /// <summary>
    /// 循环停了、连服务心跳也停了：就别再让现场去分辨"服务活着但没扫"，直接指向服务本身。
    /// </summary>
    [Fact]
    public void Stalled_loop_with_dead_service_heartbeat_asks_to_check_the_service()
    {
        _health.Report = new HealthReport(
            "unhealthy", "1.0.0", "test", "Test", 1,
            [
                new HealthCheck("config-db", true, "ok"),
                new HealthCheck("collector", false, "采集器已 120 秒没有心跳（阈值 60 秒）")
            ]);

        var (cut, _, _) = Render(new FakeSpool(), seed: SeedStalledTick);

        Assert.Contains("连采集服务心跳也停了", cut.Markup);
    }

    [Fact]
    public void Fresh_loop_is_not_flagged_as_stalled()
    {
        var (cut, _, _) = Render(new FakeSpool(), seed: diag =>
        {
            diag.PublishLoopTick(new CollectorLoopTick(
                DateTime.Now, ConfiguredIntervalMs: 200,
                WorkMs: 5, ScanMs: 3, HeartbeatMs: 1,
                PlcCount: 1, TriggeredCount: 0, CoolingDownPlcCount: 0, SkippedStationCount: 0));
        });

        Assert.Contains("最后扫描", cut.Markup);
        Assert.DoesNotContain("采集循环已停止推进", cut.Markup);
    }

    /// <summary>
    /// 被流水挤掉的失败仍要列出来。
    /// </summary>
    /// <remarks>
    /// 否则标题上的"失败 N"是个点不进去的死数字 —— 高频请求下几十条流水只覆盖几秒。
    /// </remarks>
    [Fact]
    public void Failures_pushed_out_of_the_recent_window_are_still_listed()
    {
        var (cut, _, _) = Render(new FakeSpool(), seed: diag =>
        {
            var recent = Enumerable.Range(0, 5)
                .Select(i => new PlcExchangeView(
                    new DateTime(2026, 9, 30, 10, 0, 10 + i), false, $"D{200 + i}", 1, 3, true, null, []))
                .ToList();

            diag.PublishPlcTraffic(new PlcTrafficView(
                1, "1号PLC", TotalCount: 100, FailureCount: 1, LastDurationMs: 3, MaxDurationMs: 40,
                LastSuccessAt: new DateTime(2026, 9, 30, 10, 0, 14),
                recent,
                // 比流水里最早的一条还早：它已经被挤掉了。
                [new PlcExchangeView(
                    new DateTime(2026, 9, 30, 10, 0, 0), false, "D100", 1, 40, false, "链接已断开", [])]));
        });

        Assert.Contains("失败 1", cut.Markup);
        Assert.Contains("已被流水挤掉", cut.Markup);
        // 累计失败数与明细条数的关系要写在明面上：列不出 N 条是正常的。
        Assert.Contains("失败明细最多保留最近 20 条", cut.Markup);
        Assert.Contains("链接已断开", cut.Markup);
    }

    [Fact]
    public void Failures_still_inside_the_recent_window_are_not_listed_twice()
    {
        var at = new DateTime(2026, 9, 30, 10, 0, 5);
        var failure = new PlcExchangeView(at, false, "D100", 1, 40, false, "链接已断开", []);

        var (cut, _, _) = Render(new FakeSpool(), seed: diag =>
        {
            diag.PublishPlcTraffic(new PlcTrafficView(
                1, "1号PLC", TotalCount: 10, FailureCount: 1, LastDurationMs: 40, MaxDurationMs: 40,
                LastSuccessAt: at,
                [failure],
                [failure]));
        });

        Assert.Contains("失败 1", cut.Markup);
        // 流水里已经有它了，就不该在"已被挤掉"那份里再看一遍。
        Assert.DoesNotContain("已被流水挤掉", cut.Markup);
    }

    /// <summary>
    /// 系统健康要并进来：这一页回答"现在还能不能干活"，而其中一半（配置库、数据盘、
    /// 磁盘余量、采集心跳）原本要另开一个标签页才看得到。
    /// </summary>
    [Fact]
    public void Health_block_lists_every_check()
    {
        var (cut, _, _) = Render(new FakeSpool());

        Assert.Contains("系统健康", cut.Markup);
        Assert.Contains("全部正常", cut.Markup);
        Assert.Contains("配置库", cut.Markup);
        // 报障第一句就是"哪个版本、跑了多久"：报告里现成的东西不该让用户去别处找。
        Assert.Contains("版本 1.0.0", cut.Markup);
        Assert.Contains("已运行", cut.Markup);
    }

    /// <summary>
    /// 趋势图要有两条线：每轮耗时，和当时的扫描间隔。
    /// </summary>
    /// <remarks>
    /// 间隔画成第二条线而不是静态基准线，是因为间隔可能被改过 —— 每轮用自己的值，
    /// 改动在图上是一条看得见的台阶。
    /// </remarks>
    [Fact]
    public void Loop_trend_chart_draws_work_and_interval_lines()
    {
        var (cut, _, _) = Render(new FakeSpool(), seed: diag =>
        {
            diag.PublishLoopTick(new CollectorLoopTick(
                DateTime.Now, ConfiguredIntervalMs: 200,
                WorkMs: 5, ScanMs: 3, HeartbeatMs: 1,
                PlcCount: 1, TriggeredCount: 0, CoolingDownPlcCount: 0, SkippedStationCount: 0));
        });

        Assert.Contains("每轮耗时", cut.Markup);
        Assert.Contains("扫描间隔", cut.Markup);
        // 真画出了 SVG（而不是空态文字）才算数。
        Assert.Contains("dt-chart", cut.Markup);
        Assert.DoesNotContain("还没有采集循环记录", cut.Markup);
    }

    [Fact]
    public void Loop_trend_chart_is_hidden_when_there_are_no_ticks()
    {
        var (cut, _, _) = Render(new FakeSpool());

        // 空态已有那句话，图表再渲染一遍就重复了。
        Assert.DoesNotContain("每轮耗时", cut.Markup);
    }

    [Fact]
    public void Unhealthy_check_is_called_out_with_its_reason()
    {
        _health.Report = new HealthReport(
            "unhealthy", "1.0.0", "test", "Test", 1,
            [
                new HealthCheck("config-db", true, "ok"),
                new HealthCheck("disk-free", false, "数据盘剩余 12 MB，低于 1024 MB")
            ]);

        var (cut, _, _) = Render(new FakeSpool());

        Assert.Contains("有异常项", cut.Markup);
        // 探活项的码名留给 /healthz 的机器判读，页面上要说人话。
        Assert.Contains("磁盘余量", cut.Markup);
        Assert.Contains("数据盘剩余 12 MB", cut.Markup);
    }

    /// <summary>
    /// 超量时不自动列明细。
    /// </summary>
    /// <remarks>
    /// 每两秒把几百条缓存反序列化一遍，会把诊断页自己变成磁盘和 CPU 的负担 ——
    /// 这正是这一页要防的事，所以超过阈值改成让用户显式要一次。
    /// </remarks>
    [Fact]
    public void Long_backlog_is_not_listed_automatically()
    {
        var spool = new FakeSpool { BacklogOverride = new SpoolBacklog(500, new DateTime(2026, 9, 30, 9, 0, 0)) };
        var (cut, _, _) = Render(spool);

        Assert.Contains("500 件写库失败等待补传", cut.Markup);
        Assert.Contains("列出明细", cut.Markup);
        Assert.Equal(0, spool.ListEntriesCalls);
    }

    /// <summary>
    /// 自动轮询要把新数据画出来。
    /// </summary>
    /// <remarks>
    /// 定时器不是事件回调：取完数不显式重画，字段每 2 秒都在更新而页面一直停在打开时的样子 ——
    /// 页头那句"每 2 秒自动刷新"就成了一句空话。
    /// </remarks>
    [Fact]
    public void Auto_tick_paints_the_fresh_data()
    {
        var spool = new FakeSpool();
        var (cut, _, _) = Render(spool);
        Assert.Contains("补传队列是空的", cut.Markup);

        spool.Entries.Add(Entry("stuck.spool.json", "P900", attempts: 1, error: "disk full"));

        // 两跳之内既要取到新数据，也要把它画出来。
        cut.WaitForAssertion(() => Assert.Contains("1 件写库失败等待补传", cut.Markup), TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// 页头的忙碌指示只属于"有人等着的取数"，定时轮询不点亮它。
    /// </summary>
    /// <remarks>
    /// 轮询每 2 秒路过一次，次次点亮的话，进度圈与压淡的统计就成了常态噪声 ——
    /// 这一页反而显得一直在忙。
    /// </remarks>
    [Fact]
    public void Auto_tick_does_not_light_up_the_page_busy_indicator()
    {
        var spool = new FakeSpool();
        spool.Entries.Add(Entry("stuck.spool.json", "P900", attempts: 1, error: "disk full"));
        var (cut, _, _) = Render(spool);
        Assert.Contains("立即补传", cut.Markup);

        // 挂住下一跳，等它真的开始取数。取数进行中不会有重画，等不到渲染事件，只能轮着看计数。
        var gate = new TaskCompletionSource();
        spool.Gate = gate;
        var deadline = DateTime.Now.AddSeconds(6);
        while (spool.DescribeCalls < 2 && DateTime.Now < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.True(spool.DescribeCalls >= 2, "定时跳没有在等待时间内开始");

        // 任何原因引起的重画，此刻都不该出现页头忙碌指示。
        cut.Render();
        Assert.DoesNotContain("正在刷新", cut.Markup);

        // 放行并把这一跳走完，别把未完成的取数留给测试收尾。
        gate.SetResult();
        cut.WaitForAssertion(() => Assert.True(spool.DescribeCalls >= 3), TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// 暂停刷新：冻结取数，恢复时立即取一轮而不是干等下一拍。
    /// </summary>
    /// <remarks>
    /// 现场要慢慢读表（选文本、对照日志）时，2 秒一次的刷新会把表格从手底下换掉。
    /// </remarks>
    [Fact]
    public async Task Pause_freezes_the_page_and_resume_refreshes_immediately()
    {
        var spool = new FakeSpool();
        var (cut, _, _) = Render(spool);

        await cut.FindAll("button").Single(button => button.TextContent.Contains("暂停刷新")).ClickAsync(new());
        Assert.Contains("已暂停", cut.Markup);

        var frozen = spool.DescribeCalls;
        await Task.Delay(2600);
        Assert.Equal(frozen, spool.DescribeCalls);

        await cut.FindAll("button").Single(button => button.TextContent.Contains("继续刷新")).ClickAsync(new());
        Assert.True(spool.DescribeCalls > frozen, "继续刷新应当立即取一轮，而不是等下一拍");
        Assert.DoesNotContain("已暂停", cut.Markup);
    }

    /// <summary>
    /// 暂停期间的重画不能把好着的采集循环说成"已停止推进"。
    /// </summary>
    /// <remarks>
    /// 判活用"最后扫描距今"算，而页面暂停后读数是冻着的 —— 不按快照时刻算的话，
    /// 停够一个判活窗口（≥5×扫描间隔、下限 2 秒）再随便点个按钮，就会弹出一条假警报。
    /// </remarks>
    [Fact]
    public async Task Pause_does_not_raise_a_false_loop_stall_alarm()
    {
        var spool = new FakeSpool();
        var (cut, _, _) = Render(spool, seed: diag => diag.PublishLoopTick(new CollectorLoopTick(
            DateTime.Now, ConfiguredIntervalMs: 50, WorkMs: 5, ScanMs: 3, HeartbeatMs: 1,
            PlcCount: 1, TriggeredCount: 0, CoolingDownPlcCount: 0, SkippedStationCount: 0)));

        await cut.FindAll("button").Single(button => button.TextContent.Contains("暂停刷新")).ClickAsync(new());
        // 分区说明也要跟着停：不能让"每 15 秒复查"与页头的"已暂停"打架。
        Assert.Contains("复查已暂停", cut.Markup);

        // 跨过判活窗口之后再重画一次：这一跳只可能来自页面自己，不该判出"停了"。
        await Task.Delay(2600);
        cut.Render();

        Assert.DoesNotContain("采集循环已停止推进", cut.Markup);
    }

    /// <summary>
    /// 队列在点击前被后台补空时，别说成"补传失败"。
    /// </summary>
    /// <remarks>
    /// 执行器只回条数，0 条既可能是"没人可补"也可能是"第一条就补不动" ——
    /// 提示放在收尾刷新之后，才分得清这两种。
    /// </remarks>
    [Fact]
    public async Task Replay_all_says_the_queue_is_empty_when_it_drained_first()
    {
        var spool = new FakeSpool();
        spool.Entries.Add(Entry("a.spool.json", "P900", attempts: 1, error: "disk full"));
        var (cut, _, _) = Render(spool);
        Assert.Contains("1 件写库失败等待补传", cut.Markup);

        // 页面刷新之后这条被后台补传掉了：点下去时队列其实已经空了。
        spool.Entries.Clear();
        await cut.FindAll("button").Single(button => button.TextContent.Contains("整队补传")).ClickAsync(new());

        Assert.Contains(Toast.Messages, message => message.Contains("队列已经空了"));
    }

    /// <summary>
    /// 取数进行中来的刷新请求不能被守卫丢掉。
    /// </summary>
    /// <remarks>
    /// 补传/丢弃之后的收尾刷新如果正好撞上 2 秒一跳的轮询，会被 _refreshing 守卫直接丢掉，
    /// 页面就要停在旧数据上再等一整拍 —— 用户看到的是"点了补传，界面没反应"。
    /// </remarks>
    [Fact]
    public async Task Refresh_requested_during_a_running_one_runs_right_after()
    {
        // 超量队列才有"列出明细"这个用户入口，正好用来在取数中途再触发一次刷新。
        var spool = new FakeSpool { BacklogOverride = new SpoolBacklog(500, DateTime.Now.AddMinutes(-30)) };
        var (cut, _, _) = Render(spool);
        Assert.Contains("列出明细", cut.Markup);

        // 挂住下一跳，等它真的卡在取数中途。
        var gate = new TaskCompletionSource();
        spool.Gate = gate;
        var deadline = DateTime.Now.AddSeconds(6);
        while (spool.DescribeCalls < 2 && DateTime.Now < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.True(spool.DescribeCalls >= 2, "定时跳没有在等待时间内开始");

        await cut.FindAll("button").Single(button => button.TextContent.Contains("列出明细")).ClickAsync(new());

        gate.SetResult();

        // 1.5 秒内就该看到第三轮取数：守卫吞掉请求的话，要等下一跳（约 2 秒）才会出现。
        cut.WaitForAssertion(() => Assert.True(spool.DescribeCalls >= 3), TimeSpan.FromSeconds(1.5));
    }

    /// <summary>
    /// 明细不跟着 2 秒的主刷新重读：件数没变就按自己的慢节拍来。
    /// </summary>
    /// <remarks>
    /// 读一遍明细要把队列里每条缓存和它的旁车都反序列化一次 —— 每 2 秒做一遍，
    /// 观测手段自己就成了磁盘和 CPU 的负担。件数一变则立即重读，不用等慢节拍。
    /// </remarks>
    [Fact]
    public void Spool_details_follow_count_changes_not_every_tick()
    {
        var spool = new FakeSpool();
        spool.Entries.Add(Entry("stuck.spool.json", "P900", attempts: 1, error: "disk full"));
        var (cut, _, _) = Render(spool);
        cut.WaitForAssertion(() => Assert.Contains("立即补传", cut.Markup));
        Assert.Equal(1, spool.ListEntriesCalls);
        // 明细比主刷新旧：这句话要摆在表前面，读者才知道"尝试次数"为什么可能晚几秒。
        Assert.Contains("明细每 10 秒重读", cut.Markup);

        // 等主刷新跑够两跳：件数一直没变，明细不该被重读。
        cut.WaitForAssertion(() => Assert.True(spool.DescribeCalls >= 3), TimeSpan.FromSeconds(8));
        Assert.Equal(1, spool.ListEntriesCalls);

        // 新堵进来一件：件数变了，下一跳立即重读明细。
        spool.Entries.Add(Entry("stuck2.spool.json", "P901", attempts: 2, error: "disk full"));
        cut.WaitForAssertion(() => Assert.Contains("2 件写库失败等待补传", cut.Markup), TimeSpan.FromSeconds(6));
        Assert.Equal(2, spool.ListEntriesCalls);
    }

    /// <summary>
    /// 整队补传要看得到进度，也要停得下来。
    /// </summary>
    /// <remarks>
    /// 大队列一次补几分钟时，只有一句"补传中…"既不知道走到哪了，也没法叫停。
    /// 取消只在两条之间生效：单条要整件做完 —— 半途丢下会在库里留一份、缓存又还留在盘上，
    /// 下一轮补传就会把它写第二遍。
    /// </remarks>
    [Fact]
    public async Task Replay_all_reports_progress_and_stops_when_cancelled()
    {
        var spool = new FakeSpool();
        spool.Entries.Add(Entry("a.spool.json", "P900", attempts: 1, error: "disk full"));
        spool.Entries.Add(Entry("b.spool.json", "P901", attempts: 1, error: "disk full"));
        spool.Entries.Add(Entry("c.spool.json", "P902", attempts: 1, error: "disk full"));
        var (cut, _, _) = Render(spool);

        // 第二条挂住：先看到进度，再点取消。
        var gate = new TaskCompletionSource();
        Writer.GateFrom = 2;
        Writer.Gate = gate;

        // 整队补传的点击要等整队跑完才返回，这里先不 await，等放行后再收尾。
        var replay = cut.FindAll("button").Single(button => button.TextContent.Contains("整队补传"))
            .ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("已 1 条", cut.Markup), TimeSpan.FromSeconds(3));
        await cut.FindAll("button").Single(button => button.TextContent.Contains("取消")).ClickAsync(new());

        gate.SetResult();
        await replay;

        // 取消在第二条做完之后生效：第三条留在队列里等下一轮，也不该被写进库。
        Assert.Contains(Toast.Messages, message => message.Contains("已取消补传"));
        Assert.Equal(["SN-P900", "SN-P901"], Writer.Saved);
        Assert.Equal(["c.spool.json"], spool.Entries.Select(entry => entry.FileName).ToArray());
    }

    [Fact]
    public async Task Discard_asks_for_confirmation_and_keeps_the_file_when_declined()
    {
        var spool = new FakeSpool();
        spool.Entries.Add(Entry("stuck.spool.json", "P900", attempts: 1, error: "disk full"));
        Dialogs.MessageBoxResult = false;
        var (cut, _, _) = Render(spool);
        cut.WaitForAssertion(() => Assert.Contains("立即补传", cut.Markup));

        await cut.FindAll("button").Single(b => b.TextContent.Contains("丢弃")).ClickAsync(new());

        var asked = Assert.Single(Dialogs.MessageBoxes);
        Assert.Equal("丢弃这条缓存", asked.Title);
        // 二次确认的勾选语必须把后果说全：这条记录不会进数据库，且不可恢复。
        Assert.Contains("无法恢复", asked.Acknowledge);
        Assert.Empty(spool.Deleted);
    }

    [Fact]
    public async Task Discard_deletes_the_file_after_confirmation()
    {
        var spool = new FakeSpool();
        spool.Entries.Add(Entry("stuck.spool.json", "P900", attempts: 1, error: "disk full"));
        Dialogs.MessageBoxResult = true;
        var (cut, _, _) = Render(spool);
        cut.WaitForAssertion(() => Assert.Contains("立即补传", cut.Markup));

        await cut.FindAll("button").Single(b => b.TextContent.Contains("丢弃")).ClickAsync(new());

        Assert.Equal(["stuck.spool.json"], spool.Deleted);
    }

    /// <summary>登记一轮"10 秒前就该再跑"的采集记录：配置间隔才 50 ms。</summary>
    private static void SeedStalledTick(CollectorDiagnostics diag)
        => diag.PublishLoopTick(new CollectorLoopTick(
            DateTime.Now.AddSeconds(-10), ConfiguredIntervalMs: 50,
            WorkMs: 5, ScanMs: 3, HeartbeatMs: 1,
            PlcCount: 1, TriggeredCount: 0, CoolingDownPlcCount: 0, SkippedStationCount: 0));

    private static SpoolEntry Entry(string fileName, string pallet, int attempts, string? error)
        => new(
            fileName,
            new DateTime(2026, 9, 30, 9, 30, 0),
            "202609",
            "ST010",
            pallet,
            $"SN-{pallet}",
            new DateTime(2026, 9, 30, 9, 29, 0),
            2,
            attempts,
            new DateTime(2026, 9, 30, 9, 31, 0),
            error);

    private (IRenderedComponent<Diagnostics> Cut, FakeSpool Spool, CollectorDiagnostics Diag) Render(
        FakeSpool spool,
        Action<CollectorDiagnostics>? seed = null)
    {
        var diag = new CollectorDiagnostics();
        seed?.Invoke(diag);
        Context.Services.AddLogging();
        Context.Services.AddSingleton<ICollectorDiagnostics>(diag);
        Context.Services.AddSingleton<IHealthProbe>(_health);
        Context.Services.AddSingleton<ISpoolStore>(spool);
        Context.Services.AddSingleton<ICollectWriter>(Writer);
        Context.Services.AddSingleton<SpoolReplayRunner>();

        // 页面里有 MudChip / MudTooltip / MudExpansionPanels，浮层宿主必须先渲染出来。
        // 顺序不能反：渲染宿主会初始化服务提供者，之后再注册服务会被 bUnit 直接拒绝。
        RenderPopoverHost();

        var cut = Context.RenderComponent<Diagnostics>();
        return (cut, spool, diag);
    }

    /// <summary>探活替身：报告内容由用例给定。</summary>
    private sealed class FakeHealthProbe : IHealthProbe
    {
        public HealthReport? Report { get; set; }

        public int Calls { get; private set; }

        public Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Report ?? Healthy());
        }

        private static HealthReport Healthy() => new(
            "healthy", "1.0.0", "test", "Test", 1,
            [new HealthCheck("config-db", true, "ok")]);
    }

    /// <summary>补传写入替身：记下写进去的流水号；能从第 N 条起挂住，用来观察进度与取消。</summary>
    private sealed class RecordingWriter : ICollectWriter
    {
        public List<string> Saved { get; } = [];

        /// <summary>从第几条开始挂住（1 = 第一条就挂住）。默认不挂。</summary>
        public int GateFrom { get; set; } = int.MaxValue;

        public TaskCompletionSource? Gate { get; set; }

        public int Started { get; private set; }

        public async Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
        {
            Started++;
            if (Gate is { } gate && Started >= GateFrom)
            {
                await gate.Task;
            }

            Saved.Add(request.Record.SerialNo);
        }

        public Task MarkSessionAbnormalAsync(
            string monthKey,
            long sessionId,
            DateTime endTime,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSpool : ISpoolStore
    {
        public List<SpoolEntry> Entries { get; } = [];

        public List<string> Deleted { get; } = [];

        /// <summary>不设时按 Entries 的条数推算积压。</summary>
        public SpoolBacklog? BacklogOverride { get; set; }

        /// <summary>设了它，DescribeAsync 就会挂住直到测试放行 —— 用来观察"取数进行中"的页面状态。</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>设了它，下一次 DescribeAsync 抛错 —— 用来观察读取失败时的页面与摘要。</summary>
        public bool FailNextDescribe { get; set; }

        public int DescribeCalls { get; private set; }

        public int ListEntriesCalls { get; private set; }

        public Task<string> SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult("fake.spool.json");

        public Task<IReadOnlyList<(string FileName, CollectSaveRequest Request)>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(string, CollectSaveRequest)>>(
                Entries.Select(entry => (entry.FileName, RequestFor(entry))).ToList());

        /// <summary>手动补传单条：按文件名回一条；不在时回 null，由执行器翻成"已经不在了"。</summary>
        public Task<CollectSaveRequest?> ReadOneAsync(string fileName, CancellationToken cancellationToken = default)
        {
            var entry = Entries.FirstOrDefault(item => item.FileName == fileName);
            return Task.FromResult(entry is null ? null : RequestFor(entry));
        }

        /// <summary>把替身里的一条明细还原成缓存请求：整队/单条补传都要真正写到记录里。</summary>
        private static CollectSaveRequest RequestFor(SpoolEntry entry) => new()
        {
            MonthKey = entry.MonthKey,
            Record = new CollectRecord
            {
                PalletCode = entry.PalletCode,
                SerialNo = entry.SerialNo,
                StationCode = entry.StationCode,
                TriggerTime = entry.TriggerTime,
                ResultCode = entry.ResultCode
            }
        };

        public Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
        {
            Deleted.Add(fileName);
            Entries.RemoveAll(entry => entry.FileName == fileName);
            return Task.CompletedTask;
        }

        public Task<SpoolBacklog> DescribeAsync(CancellationToken cancellationToken = default)
        {
            DescribeCalls++;
            if (FailNextDescribe)
            {
                FailNextDescribe = false;
                throw new IOException("配置库打不开");
            }

            if (Gate is { } gate)
            {
                return DescribeAfterGateAsync(gate);
            }

            return Task.FromResult(Backlog());
        }

        private async Task<SpoolBacklog> DescribeAfterGateAsync(TaskCompletionSource gate)
        {
            await gate.Task;
            return Backlog();
        }

        private SpoolBacklog Backlog()
            => BacklogOverride ?? new SpoolBacklog(
                Entries.Count,
                Entries.Count == 0 ? null : Entries.Min(entry => entry.CreatedAt));

        public Task<IReadOnlyList<SpoolEntry>> ListEntriesAsync(CancellationToken cancellationToken = default)
        {
            ListEntriesCalls++;
            return Task.FromResult<IReadOnlyList<SpoolEntry>>(Entries.ToList());
        }

        public Task NoteFailureAsync(string fileName, string error, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
