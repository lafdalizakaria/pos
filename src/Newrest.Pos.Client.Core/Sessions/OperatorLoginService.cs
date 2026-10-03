using CommunityToolkit.Mvvm.ComponentModel;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Client.Core.Sessions;

public sealed record LoggedOperator(Guid Id, string Code, string DisplayName, OperatorRoles Roles)
{
    public bool IsSupervisor => (Roles & (OperatorRoles.Supervisor | OperatorRoles.Admin)) != 0;
}

public enum LoginOutcome
{
    Success,
    UnknownOperator,
    InvalidPin,
    LockedOut,
    Inactive,
}

/// <summary>Operator login with the PIN hashes cached on the register (works offline).</summary>
public sealed partial class OperatorLoginService(ReferenceCache cache, RegisterOptions options, TimeProvider clock) : ObservableObject
{
    private readonly Dictionary<Guid, (int Failures, DateTimeOffset? LockedUntil)> _attempts = [];
    private readonly IPinHasher _hasher = new Pbkdf2PinHasher();

    [ObservableProperty]
    private LoggedOperator? _current;

    public async Task<LoginOutcome> LoginAsync(string code, string pin, CancellationToken ct = default)
    {
        var op = await cache.FindOperatorAsync(code ?? string.Empty, ct);
        if (op is null)
        {
            return LoginOutcome.UnknownOperator;
        }

        if (!op.IsActive)
        {
            return LoginOutcome.Inactive;
        }

        var now = clock.GetUtcNow();
        var (failures, lockedUntil) = _attempts.GetValueOrDefault(op.Id);
        if (lockedUntil is { } until && until > now)
        {
            return LoginOutcome.LockedOut;
        }

        if (await Task.Run(() => _hasher.Verify(pin ?? string.Empty, op.PinHash), ct))
        {
            _attempts.Remove(op.Id);
            Current = new LoggedOperator(op.Id, op.Code, $"{op.FirstName} {op.LastName}", (OperatorRoles)op.Roles);
            return LoginOutcome.Success;
        }

        failures++;
        if (failures >= options.PinMaxFailedAttempts)
        {
            _attempts[op.Id] = (0, now + options.PinLockoutDuration);
            return LoginOutcome.LockedOut;
        }

        _attempts[op.Id] = (failures, null);
        return LoginOutcome.InvalidPin;
    }

    public void Logout() => Current = null;
}
