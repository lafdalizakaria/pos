using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Audit;

namespace Newrest.Pos.Application.Accounts;

/// <summary>
/// Server-side ledger. Every operation is atomic (balance check + movement insert + cache update in one
/// transaction under a row lock) and idempotent on <see cref="MovementRequest.IdempotencyKey"/>.
/// </summary>
public interface IAccountLedger
{
    /// <exception cref="InsufficientFundsException">The debit exceeds balance + overdraft.</exception>
    /// <exception cref="AccountNotFoundException">Unknown account.</exception>
    /// <exception cref="IdempotencyConflictException">The key was already used for a different movement.</exception>
    /// <param name="audit">Optional audit entry committed in the same transaction as the movement (not written on replays).</param>
    Task<LedgerPostResult> PostAsync(Guid accountId, MovementRequest request, AuditLog? audit = null, CancellationToken cancellationToken = default);

    /// <summary>Cancels a movement with an opposite correction. Fails if it was already reversed.</summary>
    Task<LedgerPostResult> ReverseAsync(Guid movementId, Guid idempotencyKey, string performedBy, string reason,
        AuditLog? audit = null, CancellationToken cancellationToken = default);

    Task<AccountBalance> GetBalanceAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Accounts whose cached balance differs from the sum of their movements (should always be empty).</summary>
    Task<IReadOnlyList<AccountBalance>> FindCacheDriftAsync(CancellationToken cancellationToken = default);
}

/// <param name="WasDuplicate">True when the idempotency key had already been processed; the original movement is returned.</param>
public sealed record LedgerPostResult(Guid MovementId, Guid AccountId, decimal Amount, decimal BalanceAfter, bool WasDuplicate, bool ExceededOverdraft);

public sealed record AccountBalance(Guid AccountId, decimal LedgerBalance, decimal CachedBalance, decimal OverdraftLimit)
{
    public decimal AvailableToSpend => LedgerBalance + OverdraftLimit;
}

public sealed class AccountNotFoundException(Guid accountId)
    : Domain.Common.DomainException("account_not_found", $"Account {accountId} was not found.");

public sealed class IdempotencyConflictException(Guid key)
    : Domain.Common.DomainException("idempotency_conflict", $"Idempotency key {key} was already used for a different operation.");
