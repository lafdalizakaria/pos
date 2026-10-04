using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Newrest.Pos.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> only. No database is needed to add a migration.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PosDb")
                               ?? "Server=localhost;Database=NewrestPos;Integrated Security=true;TrustServerCertificate=true";
        var options = DependencyInjection.ConfigureDbContext(new DbContextOptionsBuilder<PosDbContext>(), connectionString);
        return new PosDbContext((DbContextOptions<PosDbContext>)options.Options);
    }
}
