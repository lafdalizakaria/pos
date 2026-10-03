using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Security;

public static class AuthenticationSetup
{
    public const string UsersScheme = "Users";
    public const string RegistersScheme = "Registers";
    public const string SelectorScheme = "Bearer";
    public const string BackOfficePolicy = "BackOffice";
    public const string RegisterPolicy = "Register";

    public static IServiceCollection AddPosAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var users = configuration.GetSection(UserAuthOptions.Section).Get<UserAuthOptions>() ?? new UserAuthOptions();
        var registers = configuration.GetSection(RegisterAuthOptions.Section).Get<RegisterAuthOptions>() ?? new RegisterAuthOptions();
        var registerKey = ResolveRegisterKey(registers, environment);
        services.AddSingleton(new RegisterTokenIssuer(registers, registerKey));

        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, SelectorScheme, o => o.ForwardDefaultSelector = context =>
            {
                // Route the token to the right validator by its (unvalidated) issuer; validation happens in the target scheme.
                var header = context.Request.Headers.Authorization.ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var handler = new JsonWebTokenHandler();
                    var raw = header["Bearer ".Length..].Trim();
                    if (handler.CanReadToken(raw) && handler.ReadJsonWebToken(raw).Issuer == registers.Issuer)
                    {
                        return RegistersScheme;
                    }
                }

                return UsersScheme;
            })
            .AddJwtBearer(RegistersScheme, o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = registers.Issuer,
                    ValidAudience = registers.Audience,
                    IssuerSigningKey = registerKey,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RoleClaimType = PosClaims.Roles,
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            })
            .AddJwtBearer(UsersScheme, o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    RoleClaimType = PosClaims.Roles,
                    NameClaimType = PosClaims.PreferredUserName,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };

                if (!string.IsNullOrWhiteSpace(users.Authority))
                {
                    o.Authority = users.Authority;
                    o.Audience = users.Audience;
                    o.TokenValidationParameters.ValidAudience = users.Audience;
                }
                else if (!string.IsNullOrWhiteSpace(users.DevSigningKey))
                {
                    if (environment.IsProduction())
                    {
                        throw new InvalidOperationException("Authentication:Users:DevSigningKey is not allowed in Production; configure Entra ID.");
                    }

                    o.TokenValidationParameters.ValidIssuer = users.DevIssuer;
                    o.TokenValidationParameters.ValidAudience = users.Audience ?? "newrest-pos-api";
                    o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(users.DevSigningKey));
                    o.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
                }
                else if (environment.IsProduction())
                {
                    throw new InvalidOperationException("Authentication:Users:Authority (Entra ID) must be configured in Production.");
                }
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(BackOfficePolicy, p => p.AddAuthenticationSchemes(SelectorScheme).RequireRole(PosRoles.BackOffice))
            .AddPolicy(RegisterPolicy, p => p.AddAuthenticationSchemes(SelectorScheme).RequireRole(PosRoles.Register)
                .RequireClaim(PosClaims.RegisterId));
        return services;
    }

    private static SymmetricSecurityKey ResolveRegisterKey(RegisterAuthOptions options, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.SigningKey))
        {
            var bytes = Encoding.UTF8.GetBytes(options.SigningKey);
            return bytes.Length >= 32
                ? new SymmetricSecurityKey(bytes)
                : throw new InvalidOperationException("Authentication:Registers:SigningKey must be at least 32 bytes.");
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Authentication:Registers:SigningKey must be configured (secret store / environment variable).");
        }

        // Development only: ephemeral key, register tokens become invalid when the API restarts.
        return new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
    }
}
