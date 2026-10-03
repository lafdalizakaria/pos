using System.Diagnostics;
using Newrest.Pos.Application.Abstractions;

namespace Newrest.Pos.Api.Security;

/// <summary><see cref="ICurrentUser"/> built from the validated bearer token of the current request.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string Name => Principal is { } p
        ? p.FindFirst(PosClaims.PreferredUserName)?.Value ?? p.FindFirst("upn")?.Value ?? p.FindFirst("email")?.Value
          ?? p.FindFirst("sub")?.Value ?? "unknown"
        : "anonymous";

    public IReadOnlyCollection<string> Roles => Principal?.FindAll(PosClaims.Roles).Select(c => c.Value).ToHashSet() ?? [];

    public Guid? RegisterId => Guid.TryParse(Principal?.FindFirst(PosClaims.RegisterId)?.Value, out var id) ? id : null;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? CorrelationId => Activity.Current?.TraceId.ToString() ?? accessor.HttpContext?.TraceIdentifier;
}
