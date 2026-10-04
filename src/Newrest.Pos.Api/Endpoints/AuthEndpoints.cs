using Newrest.Pos.Api.Security;
using Newrest.Pos.Application.Organization;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Endpoints;

public static class AuthEndpoints
{
    public const string TokenRateLimit = "register-token";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Authentication");

        group.MapPost("/register-token", async (RegisterTokenRequest request, RegisterAuthService auth, RegisterTokenIssuer issuer,
                TimeProvider clock, CancellationToken ct) =>
            await auth.AuthenticateAsync(request.RegisterId, request.DeviceKey, ct) is { } register
                ? Results.Ok(issuer.Issue(register, clock.GetUtcNow()))
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "invalid_device_key",
                    detail: "Unknown or inactive register, or invalid device key.",
                    extensions: new Dictionary<string, object?> { ["code"] = "invalid_device_key" }))
            .AllowAnonymous()
            .RequireRateLimiting(TokenRateLimit)
            .WithSummary("Exchanges a register device key for a short-lived access token.");

        group.MapGet("/me", (OrganizationService service, CancellationToken ct) => service.GetCurrentUserAsync(ct))
            .RequireAuthorization(AuthenticationSetup.BackOfficePolicy)
            .WithSummary("Current back-office user, roles and scopes.");

        group.MapGet("/register/me", (HttpContext context) => Results.Ok(new
        {
            RegisterId = context.User.FindFirst(PosClaims.RegisterId)?.Value,
            PointOfSaleId = context.User.FindFirst(PosClaims.PointOfSaleId)?.Value,
            SiteId = context.User.FindFirst(PosClaims.SiteId)?.Value,
            CompanyId = context.User.FindFirst(PosClaims.CompanyId)?.Value,
        }))
            .RequireAuthorization(AuthenticationSetup.RegisterPolicy)
            .WithSummary("Identity of the calling register (connectivity check).");
        return api;
    }
}
