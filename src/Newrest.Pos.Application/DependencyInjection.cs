using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Application.Accounts;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Catalog;
using Newrest.Pos.Application.Clients;
using Newrest.Pos.Application.Menus;
using Newrest.Pos.Application.Organization;
using Newrest.Pos.Application.Security;

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
        return services;
    }
}
