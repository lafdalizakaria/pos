using Newrest.Pos.Application.Organization;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Endpoints;

public static class OrganizationEndpoints
{
    public static RouteGroupBuilder MapOrganizationEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup(string.Empty).WithTags("Organisation");

        g.MapGet("/companies", (OrganizationService s, CancellationToken ct) => s.ListCompaniesAsync(ct));
        g.MapPost("/companies", async (CompanyUpsert r, OrganizationService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateCompanyAsync(r, ct)));
        g.MapPut("/companies/{id:guid}", (Guid id, CompanyUpsert r, OrganizationService s, CancellationToken ct) => s.UpdateCompanyAsync(id, r, ct));

        g.MapGet("/sites", (Guid? companyId, OrganizationService s, CancellationToken ct) => s.ListSitesAsync(companyId, ct));
        g.MapPost("/sites", async (SiteUpsert r, OrganizationService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateSiteAsync(r, ct)));
        g.MapPut("/sites/{id:guid}", (Guid id, SiteUpsert r, OrganizationService s, CancellationToken ct) => s.UpdateSiteAsync(id, r, ct));

        g.MapGet("/points-of-sale", (Guid? siteId, OrganizationService s, CancellationToken ct) => s.ListPointsOfSaleAsync(siteId, ct));
        g.MapPost("/points-of-sale", async (PointOfSaleUpsert r, OrganizationService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreatePointOfSaleAsync(r, ct)));
        g.MapPut("/points-of-sale/{id:guid}", (Guid id, PointOfSaleUpsert r, OrganizationService s, CancellationToken ct) =>
            s.UpdatePointOfSaleAsync(id, r, ct));

        g.MapGet("/registers", (Guid? pointOfSaleId, OrganizationService s, CancellationToken ct) => s.ListRegistersAsync(pointOfSaleId, ct));
        g.MapPost("/registers", async (RegisterUpsert r, OrganizationService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateRegisterAsync(r, ct)));
        g.MapPut("/registers/{id:guid}", (Guid id, RegisterUpsert r, OrganizationService s, CancellationToken ct) => s.UpdateRegisterAsync(id, r, ct));
        g.MapPost("/registers/{id:guid}/device-key", (Guid id, OrganizationService s, CancellationToken ct) => s.IssueDeviceKeyAsync(id, ct))
            .WithSummary("Issues a new device key (returned once). The previous key stops working.");
        g.MapDelete("/registers/{id:guid}/device-key", async (Guid id, OrganizationService s, CancellationToken ct) =>
        {
            await s.RevokeDeviceKeyAsync(id, ct);
            return Results.NoContent();
        });

        g.MapGet("/operators", (Guid? companyId, OrganizationService s, CancellationToken ct) => s.ListOperatorsAsync(companyId, ct));
        g.MapPost("/operators", async (OperatorCreate r, OrganizationService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateOperatorAsync(r, ct)));
        g.MapPut("/operators/{id:guid}", (Guid id, OperatorUpdate r, OrganizationService s, CancellationToken ct) => s.UpdateOperatorAsync(id, r, ct));
        g.MapPut("/operators/{id:guid}/pin", async (Guid id, SetPinRequest r, OrganizationService s, CancellationToken ct) =>
        {
            await s.SetOperatorPinAsync(id, r.Pin, ct);
            return Results.NoContent();
        });
        g.MapPost("/operators/{id:guid}/unlock", async (Guid id, OrganizationService s, CancellationToken ct) =>
        {
            await s.UnlockOperatorAsync(id, ct);
            return Results.NoContent();
        });

        g.MapGet("/access-scopes", (OrganizationService s, CancellationToken ct) => s.ListScopesAsync(ct));
        g.MapPost("/access-scopes", async (AccessScopeCreate r, OrganizationService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.GrantScopeAsync(r, ct)));
        g.MapDelete("/access-scopes/{id:guid}", async (Guid id, OrganizationService s, CancellationToken ct) =>
        {
            await s.RevokeScopeAsync(id, ct);
            return Results.NoContent();
        });
        return api;
    }
}
