using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Newrest.Pos.Application.Organization;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Security;

public sealed class RegisterTokenIssuer(RegisterAuthOptions options, SecurityKey key)
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = new(key, SecurityAlgorithms.HmacSha256);

    public RegisterTokenResponse Issue(AuthenticatedRegister register, DateTimeOffset now)
    {
        var expires = now.AddMinutes(options.TokenLifetimeMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _credentials,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, $"register:{register.Code}:{register.RegisterId}"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(PosClaims.Roles, PosRoles.Register),
                new Claim(PosClaims.RegisterId, register.RegisterId.ToString()),
                new Claim(PosClaims.PointOfSaleId, register.PointOfSaleId.ToString()),
                new Claim(PosClaims.SiteId, register.SiteId.ToString()),
                new Claim(PosClaims.CompanyId, register.CompanyId.ToString()),
            ]),
        });
        return new RegisterTokenResponse(token, expires, register.RegisterId, register.PointOfSaleId, register.SiteId, register.CompanyId,
            register.TicketPrefix);
    }
}
