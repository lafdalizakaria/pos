namespace Newrest.Pos.Contracts.V1.Sync;

// ----- Server → register ---------------------------------------------------------------------------------------------

/// <summary>Identity of the register and the fiscal mentions printed on its receipts.</summary>
public sealed record RegisterProfileDto(
    Guid RegisterId, string Code, string Name, string TicketPrefix,
    Guid PointOfSaleId, string PointOfSaleName, Guid SiteId, string SiteName, string? SiteAddress, string SiteTimeZone,
    Guid CompanyId, string CompanyName, string LegalName, string? Ice, string? TaxId, string? TradeRegister, string? CompanyAddress,
    long LastSyncedTicketSequence, string? LastSyncedTicketHash, int LastZNumber);

public sealed record SyncCategoryDto(Guid Id, string Code, string Name, int DisplayOrder, string? ColorHex, bool IsActive);

public sealed record SyncArticleDto(Guid Id, string Code, string Name, string? ReceiptLabel, Guid CategoryId, decimal BasePrice,
    decimal VatRate, bool IsSubsidizable, bool IsActive);

/// <summary>Operators of the register's company (PIN hash included for offline login).</summary>
public sealed record SyncOperatorDto(Guid Id, Guid? SiteId, string Code, string FirstName, string LastName, int Roles, string PinHash, bool IsActive);

public sealed record SyncContractDto(Guid Id, Guid ClientCompanyId, DateOnly StartDate, DateOnly? EndDate, bool IsActive, bool AcceptsThisPointOfSale);

public sealed record SyncDinerDto(Guid Id, Guid ClientCompanyId, string EmployeeNumber, string FirstName, string LastName, string? Category, bool IsActive);

public sealed record SyncBadgeDto(Guid Id, Guid DinerId, string Number, string Status);

public sealed record SyncAccountDto(Guid Id, Guid DinerId, Guid ContractId, string Type, decimal OverdraftLimit, decimal Balance, bool IsActive);

/// <param name="Cursor">Opaque: send it back on the next call to receive only what changed.</param>
/// <param name="IsFull">True when the register must replace its cache (first sync, expired or invalid cursor).</param>
/// <param name="Menus">Always the full set of published menus of the point of sale from yesterday to tomorrow.</param>
public sealed record ReferenceSyncResponse(
    string Cursor, bool IsFull, DateTimeOffset ServerTime,
    IReadOnlyList<SyncCategoryDto> Categories,
    IReadOnlyList<SyncArticleDto> Articles,
    IReadOnlyList<SyncOperatorDto> Operators,
    IReadOnlyList<SyncContractDto> Contracts,
    IReadOnlyList<SubsidyRuleDto> SubsidyRules,
    IReadOnlyList<SyncDinerDto> Diners,
    IReadOnlyList<SyncBadgeDto> Badges,
    IReadOnlyList<SyncAccountDto> Accounts,
    IReadOnlyList<DailyMenuDto> Menus);

/// <summary>Online badge lookup: freshest balance and subsidy already granted today on every point of sale.</summary>
public sealed record BadgeContextDto(
    SyncDinerDto Diner, SyncBadgeDto Badge, SyncAccountDto? Account, bool ContractAcceptsPointOfSale,
    decimal SubsidyGrantedToday, int SubsidizedMealsToday, IReadOnlyList<SubsidyRuleDto> SubsidyRules);

// ----- Register → server ---------------------------------------------------------------------------------------------

public sealed record CashSessionSyncDto(Guid Id, Guid OperatorId, DateTimeOffset OpenedAt, DateOnly BusinessDate, decimal OpeningFloat);

public sealed record TicketLineSyncDto(Guid ArticleId, string ArticleCode, string Label, int Quantity, decimal UnitPrice, decimal VatRate,
    bool IsSubsidizable, string Source);

public sealed record PaymentSyncDto(string Method, decimal Amount, decimal? Tendered, string? AuthorizationCode, Guid? AccountMovementId);

/// <summary>Complete fiscal ticket as issued by the register. The server rebuilds it and checks the hash and the chain.</summary>
public sealed record TicketSyncDto(
    Guid Id, string Kind, long Sequence, string Number, string PreviousHash, string Hash,
    Guid CashSessionId, Guid OperatorId, DateOnly BusinessDate, DateTimeOffset IssuedAt,
    Guid? DinerId, Guid? AccountId, string? BadgeNumber, Guid? SubsidyRuleId, decimal SubsidyAmount,
    Guid? CreditedTicketId, string? CreditReason,
    IReadOnlyList<TicketLineSyncDto> Lines, IReadOnlyList<PaymentSyncDto> Payments);

/// <summary>Account movement made at the register (diner share of a ticket, top-up, credit-note refund).</summary>
/// <param name="IsOfflineReplay">True when the register could not reach the server at the time of the sale.</param>
public sealed record AccountMovementSyncDto(Guid IdempotencyKey, Guid AccountId, string Type, decimal Amount, DateTimeOffset OccurredAt,
    string? PaymentMethod, Guid? TicketId, Guid? OperatorId, string? Comment, bool IsOfflineReplay);

/// <summary>Z report computed by the register; the server recomputes it from the received tickets and compares.</summary>
public sealed record ZReportSyncDto(Guid Id, Guid CashSessionId, int ZNumber, DateTimeOffset GeneratedAt, decimal CountedCash,
    Guid ClosedByOperatorId, bool Forced, long? LastTicketSequence, decimal NetSales, decimal TotalVat, decimal ExpectedCash);

public sealed record SyncAck(Guid Id, bool WasDuplicate, string? Detail = null);
