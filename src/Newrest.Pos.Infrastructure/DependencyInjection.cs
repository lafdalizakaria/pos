using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Application.Accounts;
using Newrest.Pos.Domain.Security;
using Newrest.Pos.Infrastructure.Accounts;
using Newrest.Pos.Infrastructure.Persistence;
using Newrest.Pos.Infrastructure.Persistence.Interceptors;

namespace Newrest.Pos.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "PosDb";

    public static IServiceCollection AddPosInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
                               ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PosSaveChangesInterceptor>();
        services.AddDbContext<PosDbContext>((sp, options) => ConfigureDbContext(options, connectionString)
            .AddInterceptors(sp.GetRequiredService<PosSaveChangesInterceptor>()));
        services.AddScoped<IAccountLedger, AccountLedger>();
        services.AddSingleton<IPinHasher>(_ => new Pbkdf2PinHasher(
            configuration.GetValue("Security:PinHashIterations", Pbkdf2PinHasher.DefaultIterations)));
        return services;
    }

    public static DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsHistoryTable("__EFMigrationsHistory", PosDbContext.Schema);
            sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
        });
}
