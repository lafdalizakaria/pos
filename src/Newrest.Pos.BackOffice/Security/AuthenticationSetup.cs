using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.BackOffice.Security;

public sealed class BackOfficeAuthOptions
{
    public const string Section = "Authentication";

    /// <summary>Entra ID: https://login.microsoftonline.com/{tenant-id}/v2.0</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Secret store / environment variable only.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Development only: local login form choosing a user and roles. Refused in Production.</summary>
    public bool DevLoginEnabled { get; set; }
}

public static class AuthenticationSetup
{
    public const string BackOfficePolicy = "BackOffice";

    public static IServiceCollection AddBackOfficeAuthentication(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection(BackOfficeAuthOptions.Section).Get<BackOfficeAuthOptions>() ?? new BackOfficeAuthOptions();
        services.AddSingleton(options);
        var useOidc = !string.IsNullOrWhiteSpace(options.Authority);
        if (options.DevLoginEnabled && environment.IsProduction())
        {
            throw new InvalidOperationException("Authentication:DevLoginEnabled is not allowed in Production.");
        }

        if (!useOidc && !options.DevLoginEnabled)
        {
            throw new InvalidOperationException("Configure Entra ID (Authentication:Authority, ClientId, ClientSecret) or enable the development login.");
        }

        var auth = services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = useOidc ? OpenIdConnectDefaults.AuthenticationScheme : CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(o =>
            {
                o.LoginPath = useOidc ? "/account/login" : "/account/dev-login";
                o.AccessDeniedPath = "/access-denied";
                o.Cookie.Name = "newrest.pos.bo";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
            });

        if (useOidc)
        {
            auth.AddOpenIdConnect(o =>
            {
                o.Authority = options.Authority;
                o.ClientId = options.ClientId;
                o.ClientSecret = options.ClientSecret;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.SaveTokens = false;
                o.MapInboundClaims = false;
                o.Scope.Add("profile");
                o.Scope.Add("email");
                o.TokenValidationParameters.RoleClaimType = BackOfficeClaims.Roles;
                o.TokenValidationParameters.NameClaimType = BackOfficeClaims.UserName;
            });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(BackOfficePolicy, p => p.RequireAuthenticatedUser().RequireRole(PosRoles.BackOffice))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(PosRoles.BackOffice).Build());
        services.AddCascadingAuthenticationState();
        return services;
    }

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<BackOfficeAuthOptions>();
        var account = app.MapGroup("/account").AllowAnonymous();

        account.MapGet("/login", (string? returnUrl) => Results.Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme]));

        account.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return string.IsNullOrWhiteSpace(options.Authority)
                ? Results.Redirect("/")
                : Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]);
        }).DisableAntiforgery();

        if (!options.DevLoginEnabled)
        {
            return;
        }

        account.MapGet("/dev-login", (string? returnUrl) => Results.Content(DevLoginPage(SafeReturnUrl(returnUrl)), "text/html; charset=utf-8"));
        account.MapPost("/dev-login", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            var userName = form["userName"].ToString().Trim().ToLowerInvariant();
            var roles = form["roles"].Where(r => PosRoles.BackOffice.Contains(r)).ToList();
            if (string.IsNullOrWhiteSpace(userName) || roles.Count == 0)
            {
                return Results.Redirect("/account/dev-login");
            }

            var claims = new List<Claim> { new(BackOfficeClaims.UserName, userName), new("name", userName) };
            claims.AddRange(roles.Select(r => new Claim(BackOfficeClaims.Roles, r!)));
            var identity = new ClaimsIdentity(claims, "DevLogin", BackOfficeClaims.UserName, BackOfficeClaims.Roles);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect(SafeReturnUrl(form["returnUrl"]));
        }).DisableAntiforgery();
    }

    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            && !returnUrl.StartsWith("/\\", StringComparison.Ordinal) ? returnUrl : "/";

    private static string DevLoginPage(string returnUrl)
    {
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html><html lang="fr"><head><meta charset="utf-8"><title>Connexion (développement)</title>
            <link rel="stylesheet" href="/app.css"></head><body class="login"><main class="card login-card">
            <h1>Newrest POS — back-office</h1>
            <p class="warning">Connexion de <strong>développement</strong> : désactivée en production (Entra ID).</p>
            <form method="post" action="/account/dev-login">
            <label>Utilisateur (UPN)<input name="userName" value="admin@newrest.ma" required></label>
            <fieldset><legend>Rôles</legend>
            """);
        foreach (var role in PosRoles.BackOffice)
        {
            var checkedAttr = role == PosRoles.Admin ? " checked" : string.Empty;
            html.Append($"<label class=\"check\"><input type=\"checkbox\" name=\"roles\" value=\"{role}\"{checkedAttr}> {role}</label>");
        }

        html.Append($"""
            </fieldset><input type="hidden" name="returnUrl" value="{WebUtility.HtmlEncode(returnUrl)}">
            <button class="primary" type="submit">Se connecter</button></form></main></body></html>
            """);
        return html.ToString();
    }
}
