using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Infrastructure;
using Newrest.Pos.Infrastructure.Persistence;
using Newrest.Pos.Infrastructure.Seeding;

// Dedicated migration tool: the API never migrates the database at startup.
//   Newrest.Pos.Migrator                      apply pending migrations
//   Newrest.Pos.Migrator --seed-demo          apply migrations, then seed demo data (non-production only)
//   Newrest.Pos.Migrator --list               list applied / pending migrations
//   Newrest.Pos.Migrator --script <file>      write an idempotent SQL script for DBA-run deployments
// Connection string: ConnectionStrings__PosDb (environment) or appsettings.{Environment}.json.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddPosInfrastructure(builder.Configuration);
builder.Services.AddScoped<DemoDataSeeder>();
using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");
await using var scope = host.Services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();

try
{
    if (args.Contains("--list"))
    {
        foreach (var m in await db.Database.GetAppliedMigrationsAsync())
        {
            Console.WriteLine($"applied  {m}");
        }

        foreach (var m in await db.Database.GetPendingMigrationsAsync())
        {
            Console.WriteLine($"pending  {m}");
        }

        return 0;
    }

    var scriptIndex = Array.IndexOf(args, "--script");
    if (scriptIndex >= 0)
    {
        var path = args.ElementAtOrDefault(scriptIndex + 1) ?? "migrations.sql";
        await File.WriteAllTextAsync(path, db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent));
        logger.LogInformation("Idempotent migration script written to {Path}", path);
        return 0;
    }

    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    logger.LogInformation("{Count} pending migration(s)", pending.Count);
    await db.Database.MigrateAsync();
    logger.LogInformation("Database is up to date");

    if (args.Contains("--seed-demo"))
    {
        if (builder.Environment.IsProduction() && !args.Contains("--force-demo-in-production"))
        {
            logger.LogError("Refusing to seed demo data in Production");
            return 2;
        }

        await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
    }

    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migration failed");
    return 1;
}
