using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Domain.Organization;

[Flags]
public enum OperatorRoles
{
    None = 0,
    Cashier = 1,
    Supervisor = 2,
    Admin = 4,
}

public enum PinVerificationResult
{
    Success,
    InvalidPin,
    LockedOut,
    Inactive,
}

/// <summary>Register operator (cashier, supervisor). Back-office users authenticate via Entra ID instead.</summary>
public sealed class Operator : ReferenceEntity
{
    private Operator()
    {
    }

    public Operator(Guid id, Guid companyId, string code, string firstName, string lastName, OperatorRoles roles, string pinHash)
        : base(id)
    {
        CompanyId = Guard.NotEmpty(companyId, nameof(companyId));
        Code = Guard.NotBlank(code, nameof(code), 32).ToUpperInvariant();
        FirstName = Guard.NotBlank(firstName, nameof(firstName), 100);
        LastName = Guard.NotBlank(lastName, nameof(lastName), 100);
        Roles = roles == OperatorRoles.None ? throw new DomainException("role_required", "An operator needs at least one role.") : roles;
        PinHash = Guard.NotBlank(pinHash, nameof(pinHash), 256);
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    /// <summary>Optional restriction to a single site (null = every site of the company).</summary>
    public Guid? SiteId { get; set; }

    public string Code { get; private set; } = null!;
    public string FirstName { get; private set; } = null!;
    public string LastName { get; private set; } = null!;
    public OperatorRoles Roles { get; private set; }
    public string PinHash { get; private set; } = null!;
    public int FailedPinAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public bool IsActive { get; set; }

    public string DisplayName => $"{FirstName} {LastName}";

    public bool HasRole(OperatorRoles role) => (Roles & role) == role;

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public PinVerificationResult VerifyPin(string pin, IPinHasher hasher, PinLockoutPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(policy);

        if (!IsActive)
        {
            return PinVerificationResult.Inactive;
        }

        if (IsLockedOut(now))
        {
            return PinVerificationResult.LockedOut;
        }

        if (hasher.Verify(pin, PinHash))
        {
            FailedPinAttempts = 0;
            LockedUntil = null;
            return PinVerificationResult.Success;
        }

        FailedPinAttempts++;
        if (FailedPinAttempts >= policy.MaxFailedAttempts)
        {
            FailedPinAttempts = 0;
            LockedUntil = now + policy.EffectiveLockoutDuration;
            return PinVerificationResult.LockedOut;
        }

        return PinVerificationResult.InvalidPin;
    }

    public void ChangePin(string newPin, IPinHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        PinHash = hasher.Hash(newPin);
        FailedPinAttempts = 0;
        LockedUntil = null;
    }

    public void Rename(string firstName, string lastName)
    {
        FirstName = Guard.NotBlank(firstName, nameof(firstName), 100);
        LastName = Guard.NotBlank(lastName, nameof(lastName), 100);
    }

    public void SetRoles(OperatorRoles roles) =>
        Roles = roles == OperatorRoles.None ? throw new DomainException("role_required", "An operator needs at least one role.") : roles;

    public void Unlock()
    {
        FailedPinAttempts = 0;
        LockedUntil = null;
    }
}
