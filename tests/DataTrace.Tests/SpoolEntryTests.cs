using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Storage;

namespace DataTrace.Tests;

/// <summary>
/// 补传队列的明细：卡住的是哪几件、试了几次、上次为什么失败。
/// </summary>
/// <remarks>
/// 这组断言围绕"失败记录必须跟着主文件走"：孤儿旁车会让诊断页永远等不到那次的失败原因，
/// 而只数主文件的 DescribeAsync 又会把它算成一件新的积压。
/// </remarks>
public class SpoolEntryTests
{
    private static CollectSaveRequest Request(string palletCode, short resultCode = ResultCodes.Success) => new()
    {
        MonthKey = "202609",
        Record = new CollectRecord
        {
            PalletCode = palletCode,
            SerialNo = $"SN-{palletCode}",
            StationId = 10,
            StationCode = "ST010",
            TriggerTime = new DateTime(2026, 9, 30, 10, 0, 0),
            ResultCode = resultCode,
            Judgement = Judgement.Ok
        }
    };

    [Fact]
    public async Task Entries_describe_what_is_stuck()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("spool"));

        await store.SaveAsync(Request("P001"));

        var entry = Assert.Single(await store.ListEntriesAsync());
        Assert.Equal("P001", entry.PalletCode);
        Assert.Equal("SN-P001", entry.SerialNo);
        Assert.Equal("ST010", entry.StationCode);
        Assert.Equal("202609", entry.MonthKey);
        Assert.Equal(ResultCodes.Success, entry.ResultCode);
        Assert.Equal(0, entry.Attempts);
        Assert.Null(entry.LastError);
        Assert.Null(entry.LastAttemptAt);
    }

    /// <summary>
    /// 手动补传单条按文件名读一条就够。
    /// </summary>
    /// <remarks>
    /// 队列大起来之后，为找一条而把整队反序列化一遍，点击的代价会随队列长度增长；
    /// 不存在的文件回 null，由执行器翻成"已经不在了"那句人话。
    /// </remarks>
    [Fact]
    public async Task Read_one_reads_by_file_name_and_reports_missing_as_null()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("spool"));
        await store.SaveAsync(Request("P001"));
        var file = (await store.ListAsync()).Single().FileName;

        var request = await store.ReadOneAsync(file);

        Assert.NotNull(request);
        Assert.Equal("SN-P001", request!.Record.SerialNo);

        Assert.Null(await store.ReadOneAsync("20990101000000000_不存在.spool.json"));
    }

    [Fact]
    public async Task Failures_accumulate_with_a_reason()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("spool"));
        await store.SaveAsync(Request("P001"));
        var fileName = (await store.ListEntriesAsync())[0].FileName;

        await store.NoteFailureAsync(fileName, "database is locked");
        await store.NoteFailureAsync(fileName, "disk full");

        var entry = Assert.Single(await store.ListEntriesAsync());
        Assert.Equal(2, entry.Attempts);
        Assert.Equal("disk full", entry.LastError);
        Assert.NotNull(entry.LastAttemptAt);
    }

    [Fact]
    public async Task Failure_sidecar_does_not_count_as_another_backlog_row()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("spool"));
        await store.SaveAsync(Request("P001"));
        var fileName = (await store.ListEntriesAsync())[0].FileName;
        await store.NoteFailureAsync(fileName, "database is locked");

        // 旁车文件也叫 *.json；被 DescribeAsync 数成第二件的话，看板和报警会凭空多一件积压。
        Assert.Equal(1, (await store.DescribeAsync()).Count);
        Assert.Single(await store.ListEntriesAsync());
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Replaying_successfully_removes_the_entry_and_its_sidecar()
    {
        using var workspace = new TempWorkspace();
        var spoolDirectory = workspace.Path("spool");
        var store = new FileSpoolStore(spoolDirectory);
        await store.SaveAsync(Request("P001"));
        var fileName = (await store.ListEntriesAsync())[0].FileName;
        await store.NoteFailureAsync(fileName, "database is locked");

        await store.DeleteAsync(fileName);

        Assert.Empty(await store.ListEntriesAsync());
        Assert.Empty(Directory.GetFiles(spoolDirectory, "*.json"));
    }

    /// <summary>缓存文件本身读不出来时也要出现在清单里 —— 那正是最需要看见的一条。</summary>
    [Fact]
    public async Task An_unreadable_payload_still_shows_up()
    {
        using var workspace = new TempWorkspace();
        var spoolDirectory = workspace.Path("spool");
        Directory.CreateDirectory(spoolDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(spoolDirectory, "20260930100000000_deadbeef.spool.json"),
            "{ this is not json");

        var store = new FileSpoolStore(spoolDirectory);

        var entry = Assert.Single(await store.ListEntriesAsync());
        Assert.Equal("缓存文件无法解析", entry.LastError);
        Assert.Equal(1, (await store.DescribeAsync()).Count);
    }

    [Fact]
    public async Task A_missing_directory_reads_as_an_empty_queue()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("nope", "spool"));

        Assert.Empty(await store.ListEntriesAsync());
        Assert.Empty(await store.ListAsync());
        Assert.Equal(0, (await store.DescribeAsync()).Count);
    }
}
