using DataTrace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class RuntimeDbContext : DbContext
{
    public RuntimeDbContext(DbContextOptions<RuntimeDbContext> options) : base(options)
    {
    }

    public DbSet<PalletSession> PalletSessions => Set<PalletSession>();
    public DbSet<CollectRecord> CollectRecords => Set<CollectRecord>();
    public DbSet<ProductRecord> ProductRecords => Set<ProductRecord>();
    public DbSet<TagValue> TagValues => Set<TagValue>();
    public DbSet<CurveRecord> CurveRecords => Set<CurveRecord>();
    public DbSet<CurveFeature> CurveFeatures => Set<CurveFeature>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<PalletSession>(e =>
        {
            e.HasIndex(x => x.SerialNo).IsUnique();
            e.HasIndex(x => new { x.PalletCode, x.StartTime });
        });

        builder.Entity<CollectRecord>(e =>
        {
            e.HasIndex(x => x.TriggerTime);
            e.HasIndex(x => new { x.PalletCode, x.TriggerTime });
            e.HasIndex(x => x.PalletSessionId);
            e.HasIndex(x => new { x.StationId, x.TriggerTime });
            e.HasIndex(x => x.SerialNo);
            e.HasOne(x => x.PalletSession)
                .WithMany(x => x.Records)
                .HasForeignKey(x => x.PalletSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TagValue>(e =>
        {
            e.HasIndex(x => x.CollectRecordId);
            e.HasIndex(x => new { x.TagId, x.NumericValue });
        });

        builder.Entity<CurveRecord>(e => e.HasIndex(x => x.CollectRecordId));
        builder.Entity<CurveFeature>(e =>
        {
            e.HasIndex(x => x.CurveRecordId);
            // 特征表会被 SPC / 异常检测按序列维度批量扫，补一个 (角色, 序列名) 组合索引。
            e.HasIndex(x => new { x.Role, x.SeriesName });
            e.HasOne(x => x.CurveRecord)
                .WithMany(x => x.Features)
                .HasForeignKey(x => x.CurveRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ProductRecord>(e => e.HasIndex(x => x.CollectRecordId));
    }
}
