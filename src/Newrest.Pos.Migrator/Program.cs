using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Application.Compliance;
using Newrest.Pos.Infrastructure;
using Newrest.Pos.Infrastructure.Persistence;
using Newrest.Pos.Infrastructure.Seeding;

// Dedicated migration tool: the API never migrates the database at startup.
//   Newrest.Pos.Migrator                      apply pending migrations
//   Newrest.Pos.Migrator --seed-demo          apply migrations, then seed demo data (non-production only)
//   Newrest.Pos.Migrator --list               list applied / pending migrations
//   Newrest.Pos.Migrator --script <file>      write an idempotent SQL script for DBA-run deployments
//   Newrest.Pos.Migrator --verify-archive <zip> [--previous <zip>] [--key-id <id>]
//                                             verify a signed fiscal archive offline (no database needed)
// Connection string: ConnectionStrings__PosDb (environment) or appsettings.{Environment}.json.
var verifyIndex = Array.IndexOf(args, "--verify-archive");
if (verifyIndex >= 0)
{
    return VerifyArchive(args, verifyIndex);
}

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

static int VerifyArchive(string[] args, int index)
{
    string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 ? args.ElementAtOrDefault(i + 1) : null;
    var path = args.ElementAtOrDefault(index + 1) ?? throw new ArgumentException("--verify-archive <archive.zip>");
    IReadOnlyList<RegisterCheckpoint>? previous = null;
    if (Option("--previous") is { } previousPath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(previousPath);
        using var manifest = zip.GetEntry("manifest.json")!.Open();
        previous = System.Text.Json.JsonSerializer.Deserialize<ArchiveManifest>(manifest, ArchiveFormat.Json)!.Registers;
    }

    using var stream = File.OpenRead(path);
    var result = ArchiveFormat.Verify(stream, Option("--key-id"), previous);
    Console.WriteLine($"Archive : {path}");
    Console.WriteLine($"Clé     : {result.KeyId}");
    Console.WriteLine($"Contenu : {result.Tickets} ticket(s), {result.ZReports} Z, {result.Movements} mouvement(s)");
    foreach (var issue in result.Issues)
    {
        Console.WriteLine($"  ✘ {issue}");
    }

    Console.WriteLine(result.IsValid ? "RÉSULTAT : archive intègre" : "RÉSULTAT : ARCHIVE NON INTÈGRE");
    return result.IsValid ? 0 : 2;
}
