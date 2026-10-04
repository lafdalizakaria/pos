using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Newrest.Pos.Application.Abstractions;

namespace Newrest.Pos.BackOffice.Security;

/// <summary>Principal of the current HTTP request or Blazor circuit (scoped).</summary>
public sealed class UserPrincipalHolder
{
    public ClaimsPrincipal Principal { get; set; } = new(new ClaimsIdentity());
}

public sealed class BackOfficeCurrentUser(UserPrincipalHolder holder) : ICurrentUser
{
    public bool IsAuthenticated => holder.Principal.Identity?.IsAuthenticated == true;

    public string Name => holder.Principal.FindFirst(BackOfficeClaims.UserName)?.Value
                          ?? holder.Principal.FindFirst("upn")?.Value
                          ?? holder.Principal.FindFirst(ClaimTypes.Email)?.Value
                          ?? holder.Principal.Identity?.Name ?? "unknown";

    public IReadOnlyCollection<string> Roles => holder.Principal.FindAll(BackOfficeClaims.Roles).Select(c => c.Value).ToHashSet();

    public Guid? RegisterId => null;

    public string? IpAddress { get; set; }

    public string? CorrelationId => Activity.Current?.TraceId.ToString();
}

public static class BackOfficeClaims
{
    public const string Roles = "roles";
    public const string UserName = "preferred_username";
}

/// <summary>Captures the authenticated user when an interactive circuit opens.</summary>
public sealed class UserCircuitHandler(AuthenticationStateProvider authentication, UserPrincipalHolder holder) : CircuitHandler
{
    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        holder.Principal = (await authentication.GetAuthenticationStateAsync()).User;
        authentication.AuthenticationStateChanged += async task => holder.Principal = (await task).User;
    }
}

/// <summary>
/// Runs a use case in its own DI scope (fresh DbContext) on behalf of the circuit's user.
/// Blazor Server circuits are long-lived: sharing one DbContext per circuit would cause stale data and concurrency errors.
/// </summary>
public sealed class BackOfficeRunner(IServiceScopeFactory scopes, UserPrincipalHolder circuitUser)
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserPrincipalHolder>().Principal = circuitUser.Principal;
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public Task RunAsync<TService>(Func<TService, Task> action)
        where TService : notnull =>
        RunAsync<TService, bool>(async s =>
        {
            await action(s);
            return true;
        });
}
