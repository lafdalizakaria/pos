using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newrest.Pos.Domain.Security;
using Newrest.Pos.Infrastructure.Accounts;
using Newrest.Pos.Infrastructure.Persistence;
using Newrest.Pos.Infrastructure.Persistence.Interceptors;
using Newrest.Pos.Infrastructure.Seeding;
using Testcontainers.MsSql;
using Xunit;

namespace Newrest.Pos.Testing;

/// <summary>One SQL Server container for the whole run; each test class gets its own migrated database.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string CollectionName = "sqlserver";

    private readonly MsSqlContainer _container = new MsSqlBuilder(
            Environment.GetEnvironmentVariable("POS_TEST_MSSQL_IMAGE") ?? "mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<TestDatabase> CreateDatabaseAsync(bool seedDemo = false)
    {
        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "pos_" + Guid.NewGuid().ToString("N")[..12],
        };
        var database = new TestDatabase(builder.ConnectionString);
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        if (seedDemo)
        {
            await database.CreateSeeder(db).SeedAsync();
        }

        return database;
    }
}

public sealed class TestDatabase(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public TimeProvider Clock { get; } = TimeProvider.System;

    public PosDbContext CreateContext()
    {
        var options = Infrastructure.DependencyInjection.ConfigureDbContext(new DbContextOptionsBuilder<PosDbContext>(), ConnectionString)
            .AddInterceptors(new PosSaveChangesInterceptor(Clock));
        return new PosDbContext((DbContextOptions<PosDbContext>)options.Options);
    }

    public AccountLedger CreateLedger(PosDbContext db) => new(db, Clock, NullLogger<AccountLedger>.Instance);

    public DemoDataSeeder CreateSeeder(PosDbContext db) =>
        new(db, new Pbkdf2PinHasher(1_000), Clock, NullLogger<DemoDataSeeder>.Instance);
}
