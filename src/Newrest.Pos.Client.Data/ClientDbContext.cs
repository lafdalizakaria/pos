using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Newrest.Pos.Client.Data;

/// <summary>Local SQLite database of a register (cache + fiscal data + outbox).</summary>
public sealed class ClientDbContext(DbContextOptions<ClientDbContext> options) : DbContext(options)
{
    public DbSet<CacheEntry> Cache => Set<CacheEntry>();
    public DbSet<LocalSetting> Settings => Set<LocalSetting>();
    public DbSet<RegisterState> RegisterStates => Set<RegisterState>();
    public DbSet<LocalCashSession> CashSessions => Set<LocalCashSession>();
    public DbSet<LocalTicket> Tickets => Set<LocalTicket>();
    public DbSet<LocalAccountMovement> AccountMovements => Set<LocalAccountMovement>();
    public DbSet<LocalZReport> ZReports => Set<LocalZReport>();
    public DbSet<OutboxItem> Outbox => Set<OutboxItem>();

    public static DbContextOptions<ClientDbContext> CreateOptions(string databasePath) =>
        new DbContextOptionsBuilder<ClientDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no decimal type: store as TEXT to keep exact amounts.
        configurationBuilder.Properties<decimal>().HaveConversion<string>();
        // DateTimeOffset as UTC ticks so that ordering and comparisons work in SQL.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CacheEntry>(b =>
        {
            b.HasKey(x => new { x.Kind, x.Id });
            b.Property(x => x.Kind).HasConversion<string>();
            b.HasIndex(x => new { x.Kind, x.LookupKey });
        });
        modelBuilder.Entity<LocalSetting>().HasKey(x => x.Key);
        modelBuilder.Entity<RegisterState>().HasKey(x => x.Id);
        modelBuilder.Entity<LocalCashSession>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>();
        });
        modelBuilder.Entity<LocalTicket>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Sequence).IsUnique();
            b.HasIndex(x => new { x.CashSessionId, x.Sequence });
            b.HasIndex(x => new { x.DinerId, x.BusinessDate });
        });
        modelBuilder.Entity<LocalAccountMovement>(b =>
        {
            b.HasKey(x => x.IdempotencyKey);
            b.HasIndex(x => new { x.AccountId, x.Synced });
            b.HasIndex(x => x.CashSessionId);
        });
        modelBuilder.Entity<LocalZReport>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ZNumber).IsUnique();
        });
        modelBuilder.Entity<OutboxItem>(b =>
        {
            b.HasKey(x => x.Position);
            b.Property(x => x.Position).ValueGeneratedOnAdd();
            b.Property(x => x.Kind).HasConversion<string>();
            b.Property(x => x.Status).HasConversion<string>();
            b.HasIndex(x => x.ItemId).IsUnique();
            b.HasIndex(x => x.Status);
        });
    }
}

/// <summary>Used by <c>dotnet ef</c> only.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ClientDbContext>
{
    public ClientDbContext CreateDbContext(string[] args) => new(ClientDbContext.CreateOptions("design.db"));
}
