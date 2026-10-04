using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newrest.Pos.Application.Accounts;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Catalog;
using Newrest.Pos.Application.Clients;
using Newrest.Pos.Application.Menus;
using Newrest.Pos.Application.Organization;
using Newrest.Pos.Application.Sales;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Application.Sync;

namespace Newrest.Pos.Application;

public static class DependencyInjection
{
    /// <summary>Use cases shared by the API and the back-office (all scoped: one unit of work per request / circuit operation).</summary>
    public static IServiceCollection AddPosApplication(this IServiceCollection services)
    {
        services.AddScoped<AccessControl>();
        services.AddScoped<AuditTrail>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<RegisterAuthService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<MenuService>();
        services.AddScoped<ClientService>();
        services.AddScoped<DinerService>();
        services.AddScoped<AccountService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<RegisterReferenceService>();
        services.AddScoped<RegisterSyncService>();
        services.AddScoped<TicketQueryService>();
        services.AddScoped<Vision.RecognitionService>();
        services.AddScoped<Vision.VisionModelService>();
        services.AddScoped<Operations.SupervisionService>();
        services.AddScoped<Compliance.ArchiveService>();
        services.AddScoped<Compliance.PrivacyService>();
        services.TryAddSingleton(new Domain.Operations.SupervisionThresholds());
        return services;
    }
}
