namespace Newrest.Pos.Client.Data;

public enum CacheKind
{
    Category,
    Article,
    Operator,
    Contract,
    SubsidyRule,
    Diner,
    Badge,
    Account,
    Menu,
}

/// <summary>Reference data cached for offline use: one row per server object, the DTO kept as JSON.</summary>
public sealed class CacheEntry
{
    public CacheKind Kind { get; set; }
    public Guid Id { get; set; }

    /// <summary>Lookup key: badge number, operator code, diner id of an account, contract id of a rule, menu date...</summary>
    public string? LookupKey { get; set; }

    public string Json { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LocalSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// Fiscal state of the register (single row). Updated in the same SQLite transaction as the ticket it describes,
/// which is what guarantees a gapless sequence even after a crash.
/// </summary>
public sealed class RegisterState
{
    public int Id { get; set; } = 1;
    public long LastSequence { get; set; }
    public string LastHash { get; set; } = new('0', 64);
    public int LastZNumber { get; set; }
    public Guid? OpenSessionId { get; set; }
}

public enum LocalSessionStatus
{
    Open,
    Closed,
}

public sealed class LocalCashSession
{
    public Guid Id { get; set; }
    public Guid OperatorId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateOnly BusinessDate { get; set; }
    public decimal OpeningFloat { get; set; }
    public LocalSessionStatus Status { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ZReportId { get; set; }
}

public sealed class LocalTicket
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public string Number { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid CashSessionId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public Guid? DinerId { get; set; }
    public string? BadgeNumber { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal SubsidyAmount { get; set; }
    public decimal DinerShare { get; set; }
    public Guid? CreditedTicketId { get; set; }
    public string Hash { get; set; } = "";

    /// <summary>Complete <c>TicketSyncDto</c> (the ticket is rebuilt from it with the domain code).</summary>
    public string Json { get; set; } = "";
}

/// <summary>Account movement made at this register (diner share, top-up, refund), for the Z report and offline balances.</summary>
public sealed class LocalAccountMovement
{
    public Guid IdempotencyKey { get; set; }
    public Guid AccountId { get; set; }
    public string? BadgeNumber { get; set; }
    public string Type { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? PaymentMethod { get; set; }
    public Guid? TicketId { get; set; }
    public Guid? CashSessionId { get; set; }

    /// <summary>True when the server accepted it at the time (online); false = offline, waiting in the outbox.</summary>
    public bool ConfirmedOnline { get; set; }

    public bool Synced { get; set; }
}

public sealed class LocalZReport
{
    public Guid Id { get; set; }
    public Guid CashSessionId { get; set; }
    public int ZNumber { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public string Json { get; set; } = "";
}

public enum OutboxKind
{
    CashSessionOpened,
    AccountMovement,
    Ticket,
    ZReport,
}

public enum OutboxStatus
{
    Pending,
    Sent,

    /// <summary>Rejected by the server: blocks the queue (fiscal order) until a supervisor intervenes.</summary>
    Rejected,
}

/// <summary>Ordered queue of everything to send to the server; <see cref="Position"/> is the sending order.</summary>
public sealed class OutboxItem
{
    public long Position { get; set; }
    public Guid ItemId { get; set; }
    public OutboxKind Kind { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
