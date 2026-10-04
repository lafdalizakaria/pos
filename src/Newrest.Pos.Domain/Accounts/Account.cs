using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Accounts;

public enum AccountType
{
    /// <summary>Diner tops up beforehand; overdraft usually 0.</summary>
    Prepaid,

    /// <summary>Consumption billed to the employer or withheld on salary; <see cref="Account.OverdraftLimit"/> is the credit ceiling.</summary>
    Postpaid,

    /// <summary>Prepaid balance first, then postpaid up to <see cref="Account.OverdraftLimit"/>; negative balance is billed at month end.</summary>
    Mixed,
}

public enum MovementType
{
    /// <summary>Credit (cash, card, transfer from employer). Amount &gt; 0.</summary>
    TopUp,

    /// <summary>Diner share of a ticket. Amount &lt; 0.</summary>
    Consumption,

    /// <summary>Employer-funded credit on the account (allowance). Amount &gt; 0.</summary>
    Subsidy,

    /// <summary>Positive: refund of a consumption (credit note). Negative: remaining balance paid back to the diner.</summary>
    Refund,

    /// <summary>Manual or reversal correction. Any sign. Reversals reference the original movement.</summary>
    Correction,

    /// <summary>Balance moved between two accounts (one movement per side, same idempotency group). Any sign.</summary>
    Transfer,
}

public enum PaymentMethod
{
    Cash,
    Card,
    Account,
    BankTransfer,
}

/// <summary>
/// Diner account. The balance is the sum of its <see cref="AccountMovement"/>s; <see cref="CachedBalance"/>
/// is a denormalised copy updated in the same transaction as each movement insert.
/// </summary>
public sealed class Account : ReferenceEntity
{
    private Account()
    {
    }

    public Account(Guid id, Guid dinerId, Guid contractId, AccountType type, decimal overdraftLimit = 0m) : base(id)
    {
        DinerId = Guard.NotEmpty(dinerId, nameof(dinerId));
        ContractId = Guard.NotEmpty(contractId, nameof(contractId));
        Type = type;
        SetOverdraftLimit(overdraftLimit);
        IsActive = true;
    }

    public Guid DinerId { get; private set; }
    public Guid ContractId { get; private set; }
    public AccountType Type { get; set; }

    /// <summary>How far below zero the balance may go (credit ceiling for postpaid). Always &gt;= 0.</summary>
    public decimal OverdraftLimit { get; private set; }

    public decimal CachedBalance { get; private set; }
    public bool IsActive { get; set; }

    public decimal AvailableToSpend => CachedBalance + OverdraftLimit;

    public void SetOverdraftLimit(decimal limit)
    {
        Guard.NotNegative(limit, nameof(limit));
        OverdraftLimit = Money.EnsureValid(limit, nameof(limit));
    }

    public bool CanDebit(decimal amount) => amount <= AvailableToSpend;

    /// <summary>
    /// Creates a movement and applies it to <see cref="CachedBalance"/>. The caller must hold a lock
    /// (or a rowversion check) on the account row so that the check and the insert are atomic.
    /// </summary>
    public AccountMovement Post(MovementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var amount = Money.EnsureValid(request.Amount, nameof(request.Amount));
        Ledger.EnsureSignMatchesType(request.Type, amount);

        var exceeded = false;
        if (amount < 0)
        {
            if (!IsActive && !request.IsOfflineReplay)
            {
                throw new DomainException("account_inactive", "The account is inactive.");
            }

            if (!CanDebit(-amount))
            {
                if (!request.IsOfflineReplay)
                {
                    throw new InsufficientFundsException(Id, -amount, AvailableToSpend);
                }

                // A sale made offline already happened: it is recorded and flagged for follow-up.
                exceeded = true;
            }
        }

        CachedBalance += amount;
        return new AccountMovement(request, Id, CachedBalance, exceeded);
    }

    /// <summary>Cancels a movement with an opposite <see cref="MovementType.Correction"/> referencing it.</summary>
    public AccountMovement Reverse(AccountMovement original, Guid idempotencyKey, DateTimeOffset occurredAt, string performedBy, string reason)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.AccountId != Id)
        {
            throw new DomainException("movement_other_account", "The movement belongs to another account.");
        }

        if (original.ReversesMovementId is not null)
        {
            throw new DomainException("cannot_reverse_reversal", "A reversal cannot itself be reversed; post a new movement instead.");
        }

        return Post(new MovementRequest(MovementType.Correction, -original.Amount, idempotencyKey, occurredAt)
        {
            ReversesMovementId = original.Id,
            PerformedBy = Guard.NotBlank(performedBy, nameof(performedBy)),
            Comment = Guard.NotBlank(reason, nameof(reason), 500),
        });
    }

    /// <summary>Re-aligns the cache on the ledger (used by reconciliation jobs only).</summary>
    public void ResyncCachedBalance(decimal ledgerBalance) => CachedBalance = ledgerBalance;
}

public sealed record MovementRequest(MovementType Type, decimal Amount, Guid IdempotencyKey, DateTimeOffset OccurredAt)
{
    public Guid? ReversesMovementId { get; init; }
    public Guid? TicketId { get; init; }
    public Guid? RegisterId { get; init; }
    public Guid? OperatorId { get; init; }
    public string? PerformedBy { get; init; }
    public PaymentMethod? PaymentMethod { get; init; }
    public string? Comment { get; init; }

    /// <summary>True when the movement was made offline by a register and is now being synchronised.</summary>
    public bool IsOfflineReplay { get; init; }
}

/// <summary>Immutable ledger entry. Signed amount: positive = credit, negative = debit.</summary>
public sealed class AccountMovement : Entity, IImmutableRecord
{
    private AccountMovement()
    {
    }

    internal AccountMovement(MovementRequest request, Guid accountId, decimal balanceAfter, bool exceededOverdraft)
    {
        AccountId = accountId;
        Type = request.Type;
        Amount = request.Amount;
        OccurredAt = request.OccurredAt;
        IdempotencyKey = Guard.NotEmpty(request.IdempotencyKey, nameof(request.IdempotencyKey));
        ReversesMovementId = request.ReversesMovementId;
        TicketId = request.TicketId;
        RegisterId = request.RegisterId;
        OperatorId = request.OperatorId;
        PerformedBy = request.PerformedBy;
        PaymentMethod = request.PaymentMethod;
        Comment = request.Comment;
        IsOfflineReplay = request.IsOfflineReplay;
        BalanceAfter = balanceAfter;
        ExceededOverdraft = exceededOverdraft;
    }

    public Guid AccountId { get; private set; }
    public MovementType Type { get; private set; }
    public decimal Amount { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Server-side insertion timestamp (set by persistence).</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>Client-generated key; the server deduplicates on it.</summary>
    public Guid IdempotencyKey { get; private set; }

    public Guid? ReversesMovementId { get; private set; }
    public Guid? TicketId { get; private set; }
    public Guid? RegisterId { get; private set; }
    public Guid? OperatorId { get; private set; }

    /// <summary>Back-office user (UPN) for manual movements.</summary>
    public string? PerformedBy { get; private set; }

    /// <summary>How a top-up was paid (counts in the register Z report when <see cref="RegisterId"/> is set).</summary>
    public PaymentMethod? PaymentMethod { get; private set; }

    public string? Comment { get; private set; }

    /// <summary>Balance right after this movement, informative only (the sum of movements is the truth).</summary>
    public decimal BalanceAfter { get; private set; }

    public bool IsOfflineReplay { get; private set; }

    /// <summary>True when an offline replay pushed the balance below the overdraft limit.</summary>
    public bool ExceededOverdraft { get; private set; }
}

public sealed class InsufficientFundsException : DomainException
{
    public InsufficientFundsException(Guid accountId, decimal requested, decimal available)
        : base("insufficient_funds", $"Insufficient funds: requested {requested:0.00} MAD, available {available:0.00} MAD.")
    {
        AccountId = accountId;
        Requested = requested;
        Available = available;
    }

    public Guid AccountId { get; }
    public decimal Requested { get; }
    public decimal Available { get; }
}

public static class Ledger
{
    public static decimal ComputeBalance(IEnumerable<AccountMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        return movements.Sum(m => m.Amount);
    }

    public static void EnsureSignMatchesType(MovementType type, decimal amount)
    {
        var valid = type switch
        {
            MovementType.TopUp or MovementType.Subsidy => amount > 0,
            MovementType.Consumption => amount < 0,
            MovementType.Refund or MovementType.Correction or MovementType.Transfer => amount != 0,
            _ => false,
        };

        if (!valid)
        {
            throw new DomainException("invalid_movement_sign", $"Amount {amount:0.00} is not valid for a {type} movement.");
        }
    }
}
