namespace Newrest.Pos.Contracts.V1;

public sealed record ClientCompanyDto(Guid Id, Guid CompanyId, string Code, string Name, string? Ice, string? BillingAddress, bool IsActive);

public sealed record ClientCompanyUpsert(Guid CompanyId, string Code, string Name, string? Ice, string? BillingAddress, bool IsActive = true);

public sealed record SubsidyRuleDto(Guid Id, Guid ContractId, string Name, string Kind, decimal Value, decimal? MaxPerMeal,
    decimal? MaxPerDay, int? MaxMealsPerDay, string? DinerCategory, DateOnly ValidFrom, DateOnly? ValidTo);

/// <param name="Kind">Percentage or FixedAmount.</param>
public sealed record SubsidyRuleCreate(string Name, string Kind, decimal Value, DateOnly ValidFrom, decimal? MaxPerMeal,
    decimal? MaxPerDay, int? MaxMealsPerDay, string? DinerCategory, DateOnly? ValidTo);

public sealed record SubsidyRuleClose(DateOnly LastValidDate);

public sealed record ContractDto(Guid Id, Guid ClientCompanyId, string Reference, DateOnly StartDate, DateOnly? EndDate,
    string BillingMode, bool IsActive, IReadOnlyList<Guid> PointOfSaleIds, IReadOnlyList<SubsidyRuleDto> SubsidyRules);

/// <param name="BillingMode">EmployerInvoice or PayrollDeduction.</param>
public sealed record ContractUpsert(Guid ClientCompanyId, string Reference, DateOnly StartDate, DateOnly? EndDate,
    string BillingMode, IReadOnlyList<Guid> PointOfSaleIds, bool IsActive = true);

public sealed record BadgeDto(Guid Id, Guid DinerId, string Number, string Status, DateTimeOffset IssuedAt,
    DateTimeOffset? DeactivatedAt, Guid? ReplacedByBadgeId);

public sealed record DinerDto(Guid Id, Guid ClientCompanyId, string EmployeeNumber, string FirstName, string LastName,
    string? Category, bool IsActive, IReadOnlyList<BadgeDto> Badges, IReadOnlyList<AccountSummaryDto> Accounts);

/// <param name="AccountType">Prepaid, Postpaid or Mixed.</param>
public sealed record DinerCreate(Guid ClientCompanyId, Guid ContractId, string EmployeeNumber, string FirstName, string LastName,
    string? Category, string AccountType, decimal OverdraftLimit, string? BadgeNumber);

public sealed record DinerUpdate(string FirstName, string LastName, string? Category, bool IsActive);

public sealed record BadgeIssue(string Number);

public sealed record BadgeReplace(string NewNumber);

public sealed record DinerImportLineError(int Line, string EmployeeNumber, string Message);

public sealed record DinerImportResult(int Created, int Updated, int Unchanged, IReadOnlyList<DinerImportLineError> Errors);
