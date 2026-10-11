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
    public DbSet<MesOutboxIntent> MesOutboxIntents => Set<MesOutboxIntent>();
    public DbSet<SessionIndexIntent> SessionIndexIntents => Set<SessionIndexIntent>();
    public DbSet<PendingWriteBack> PendingWriteBacks => Set<PendingWriteBack>();

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
            e.HasIndex(x => x.ConfigurationRevisionId);
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
        builder.Entity<MesOutboxIntent>(e =>
        {
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.PublishedAt, x.CreatedAt });
        });
        // 在制索引意图：对账器只扫未决（AppliedAt 为空）的行，按产生顺序重放。
        // 月库新表由 RuntimeSchema v3 幂等建出（见 RuntimeSchema.ApplyVersion3），模型这里保持同一形状。
        builder.Entity<SessionIndexIntent>(e =>
        {
            e.HasIndex(x => new { x.AppliedAt, x.CreatedAt });
        });
        // 待确认回写：触发命中按工站查最早未确认件；(流水号, 工站) 唯一，同一事件绝不留两张留证。
        builder.Entity<PendingWriteBack>(e =>
        {
            e.HasIndex(x => new { x.StationId, x.ConfirmedAt });
            e.HasIndex(x => x.CollectRecordId).IsUnique();
        });
    }
}
