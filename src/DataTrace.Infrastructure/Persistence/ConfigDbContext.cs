using DataTrace.Domain.Entities;
using DataTrace.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class ConfigDbContext : IdentityDbContext<ApplicationUser>
{
    public ConfigDbContext(DbContextOptions<ConfigDbContext> options) : base(options)
    {
    }

    public DbSet<PlcConnection> PlcConnections => Set<PlcConnection>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<ProductPositionDefinition> ProductPositions => Set<ProductPositionDefinition>();
    public DbSet<TagDefinition> Tags => Set<TagDefinition>();
    public DbSet<CurveDefinition> Curves => Set<CurveDefinition>();
    public DbSet<CurveSeries> CurveSeries => Set<CurveSeries>();
    public DbSet<CurveCriterion> CurveCriteria => Set<CurveCriterion>();
    public DbSet<HeartbeatSettings> Heartbeats => Set<HeartbeatSettings>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<ActiveSessionIndex> ActiveSessions => Set<ActiveSessionIndex>();
    public DbSet<SerialCounter> SerialCounters => Set<SerialCounter>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ConfigVersion> ConfigVersions => Set<ConfigVersion>();
    public DbSet<MesOutboxItem> MesOutbox => Set<MesOutboxItem>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeLimit> RecipeLimits => Set<RecipeLimit>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PlcConnection>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.Heartbeat)
                .WithOne(x => x.PlcConnection)
                .HasForeignKey<HeartbeatSettings>(x => x.PlcConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Station>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.PlcConnection)
                .WithMany(x => x.Stations)
                .HasForeignKey(x => x.PlcConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TagDefinition>(e =>
        {
            e.HasIndex(x => new { x.StationId, x.Code }).IsUnique();
        });

        builder.Entity<CurveDefinition>(e =>
        {
            e.HasIndex(x => new { x.StationId, x.Code, x.PositionIndex }).IsUnique();
        });

        builder.Entity<CurveCriterion>(e =>
        {
            e.HasIndex(x => x.CurveDefinitionId);
            e.HasOne(x => x.CurveDefinition)
                .WithMany(x => x.Criteria)
                .HasForeignKey(x => x.CurveDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Recipe>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
        });

        builder.Entity<RecipeLimit>(e =>
        {
            // 一个型号对一个点位只能有一条覆盖行，否则解析时取值不确定。
            e.HasIndex(x => new { x.RecipeId, x.TagId }).IsUnique();
            e.HasOne(x => x.Recipe)
                .WithMany(x => x.Limits)
                .HasForeignKey(x => x.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ActiveSessionIndex>(e =>
        {
            e.HasIndex(x => x.PalletCode).IsUnique();
            e.HasIndex(x => x.SerialNo);
        });

        builder.Entity<SerialCounter>(e => e.HasIndex(x => x.DayKey).IsUnique());
        builder.Entity<AuditLog>(e => e.HasIndex(x => x.Time));
        builder.Entity<MesOutboxItem>(e => e.HasIndex(x => new { x.Status, x.CreatedAt }));
    }
}
