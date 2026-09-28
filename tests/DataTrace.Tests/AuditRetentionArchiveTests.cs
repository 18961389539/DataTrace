using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataTrace.Domain.Entities;
using DataTrace.Infrastructure.Backup;
using DataTrace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataTrace.Tests;

public sealed class AuditRetentionArchiveTests
{
    [Fact]
    public async Task Archive_is_verified_before_old_rows_are_removed()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var oldTime = new DateTime(DateTime.Now.Year - 7, 1, 15, 10, 30, 0);
        var firstOldLog = new AuditLog
        {
            Time = oldTime,
            UserName = "admin",
            Action = "Update",
            EntityType = "Station",
            EntityKey = "ST010",
            OldValue = "old,value",
            NewValue = "new"
        };
        var secondOldLog = new AuditLog
        {
            Time = oldTime.AddDays(1),
            UserName = "engineer",
            Action = "Delete",
            EntityType = "Station",
            EntityKey = "ST020"
        };
        db.AuditLogs.AddRange(
            firstOldLog,
            secondOldLog,
            new AuditLog
            {
                Time = DateTime.Now,
                UserName = "admin",
                Action = "Recent",
                EntityType = "User"
            });
        await db.SaveChangesAsync();

        var service = new AuditRetentionArchiveService(
            new AuditLogger(db),
            new DataRootPaths(workspace.Path("data")),
            NullLogger<AuditRetentionArchiveService>.Instance);

        var deleted = await service.ArchiveAndPurgeAsync(5);

        Assert.Equal(2, deleted);
        var remaining = await db.AuditLogs.AsNoTracking().ToListAsync();
        Assert.Contains(remaining, x => x.Action == "Recent");
        Assert.Contains(remaining, x => x.Action == "ArchiveAndPurge");
        Assert.DoesNotContain(remaining, x => x.Action == "Update");

        var month = oldTime.ToString("yyyyMM");
        var monthDirectory = workspace.Path("data", "audit-archive", month);
        var archiveDirectory = Assert.Single(Directory.GetDirectories(monthDirectory, "batch-*"));
        var archivePath = Path.Combine(archiveDirectory, $"AuditLogs_{month}.csv.gz");
        var manifestPath = Path.Combine(archiveDirectory, "manifest.json");
        Assert.True(File.Exists(archivePath));

        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)).RootElement;
        Assert.Equal(2, manifest.GetProperty("Rows").GetInt32());
        var expectedHash = manifest.GetProperty("Sha256").GetString();
        await using var archive = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archive));
        Assert.Equal(expectedHash, actualHash);

        await using var compressed = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var csv = await reader.ReadToEndAsync();
        Assert.StartsWith("\uFEFFId,Time,UserName", csv);
        Assert.Contains("\"old,value\"", csv);
        Assert.Contains("ST020", csv);

        // Simulate a crash after publishing the archive but before deleting its source rows.
        db.ChangeTracker.Clear();
        db.AuditLogs.AddRange(
            new AuditLog
            {
                Id = firstOldLog.Id,
                Time = firstOldLog.Time,
                UserName = firstOldLog.UserName,
                Action = firstOldLog.Action,
                EntityType = firstOldLog.EntityType,
                EntityKey = firstOldLog.EntityKey,
                OldValue = firstOldLog.OldValue,
                NewValue = firstOldLog.NewValue
            },
            new AuditLog
            {
                Id = secondOldLog.Id,
                Time = secondOldLog.Time,
                UserName = secondOldLog.UserName,
                Action = secondOldLog.Action,
                EntityType = secondOldLog.EntityType,
                EntityKey = secondOldLog.EntityKey
            },
            new AuditLog
            {
                Time = oldTime.AddDays(2),
                UserName = "system",
                Action = "LateOldRecord",
                EntityType = "AuditLog"
            });
        await db.SaveChangesAsync();

        Assert.Equal(3, await service.ArchiveAndPurgeAsync(5));
        Assert.Equal(0, await db.AuditLogs.CountAsync(
            x => x.Action == "Update" || x.Action == "Delete" || x.Action == "LateOldRecord"));
        Assert.Equal(2, Directory.GetDirectories(monthDirectory, "batch-*").Length);
    }

    [Fact]
    public async Task Invalid_existing_archive_keeps_online_rows_untouched()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var oldTime = new DateTime(DateTime.Now.Year - 7, 1, 15);
        db.AuditLogs.Add(new AuditLog
        {
            Time = oldTime,
            UserName = "admin",
            Action = "Update",
            EntityType = "Station"
        });
        await db.SaveChangesAsync();

        var month = oldTime.ToString("yyyyMM");
        var archiveDirectory = workspace.Path("data", "audit-archive", month, "batch-1-1-1");
        Directory.CreateDirectory(archiveDirectory);
        await File.WriteAllTextAsync(Path.Combine(archiveDirectory, "manifest.json"), "{}");
        var service = new AuditRetentionArchiveService(
            new AuditLogger(db),
            new DataRootPaths(workspace.Path("data")),
            NullLogger<AuditRetentionArchiveService>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ArchiveAndPurgeAsync(5));
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.Action == "Update"));
        Assert.Contains(await db.AuditLogs.AsNoTracking().ToListAsync(),
            x => x.Action == "ArchiveAndPurge" && x.Outcome == "Failure");
    }
}
