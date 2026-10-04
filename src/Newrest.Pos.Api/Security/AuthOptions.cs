using System.ComponentModel.DataAnnotations;

namespace Newrest.Pos.Api.Security;

/// <summary>Back-office users: Entra ID (OpenID Connect) access tokens.</summary>
public sealed class UserAuthOptions
{
    public const string Section = "Authentication:Users";

    /// <summary>e.g. https://login.microsoftonline.com/{tenant-id}/v2.0</summary>
    public string? Authority { get; set; }

    /// <summary>Application ID URI or client id of the API app registration.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Development / automated tests only: validates HS256 tokens signed with this key instead of Entra ID.
    /// Refused in Production.
    /// </summary>
    public string? DevSigningKey { get; set; }

    public string DevIssuer { get; set; } = "newrest-pos-dev";
}

/// <summary>Short-lived tokens issued by this API to registers in exchange for their device key.</summary>
public sealed class RegisterAuthOptions
{
    public const string Section = "Authentication:Registers";

    public string Issuer { get; set; } = "newrest-pos";

    public string Audience { get; set; } = "newrest-pos-registers";

    /// <summary>HMAC-SHA256 key, at least 32 bytes (secret: environment variable or key vault, never in appsettings).</summary>
    public string? SigningKey { get; set; }

    [Range(1, 60)]
    public int TokenLifetimeMinutes { get; set; } = 15;
}

public static class PosClaims
{
    public const string Roles = "roles";
    public const string RegisterId = "register_id";
    public const string PointOfSaleId = "pos_id";
    public const string SiteId = "site_id";
    public const string CompanyId = "company_id";
    public const string PreferredUserName = "preferred_username";
}
