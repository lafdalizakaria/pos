namespace Newrest.Pos.Contracts.V1;

public sealed record CompanyDto(Guid Id, string Code, string Name, string LegalName, string? Ice, string? TaxId,
    string? TradeRegister, string? Address, bool IsActive);

public sealed record CompanyUpsert(string Code, string Name, string LegalName, string? Ice, string? TaxId,
    string? TradeRegister, string? Address, bool IsActive = true);

public sealed record SiteDto(Guid Id, Guid CompanyId, string Code, string Name, string City, string? Address, string TimeZone, bool IsActive);

public sealed record SiteUpsert(Guid CompanyId, string Code, string Name, string City, string? Address, string? TimeZone, bool IsActive = true);

public sealed record PointOfSaleDto(Guid Id, Guid SiteId, string Code, string Name, string Type, bool IsActive);

public sealed record PointOfSaleUpsert(Guid SiteId, string Code, string Name, string Type, bool IsActive = true);

public sealed record RegisterDto(Guid Id, Guid PointOfSaleId, string Code, string Name, string TicketPrefix, bool IsActive,
    bool HasDeviceKey, DateTimeOffset? DeviceKeyIssuedAt, DateTimeOffset? LastSeenAt, long LastSyncedTicketSequence);

public sealed record RegisterUpsert(Guid PointOfSaleId, string Code, string Name, string TicketPrefix, bool IsActive = true);

/// <summary>Returned once: the key is never stored in clear text nor shown again.</summary>
public sealed record DeviceKeyIssued(Guid RegisterId, string DeviceKey, DateTimeOffset IssuedAt);

public sealed record OperatorDto(Guid Id, Guid CompanyId, Guid? SiteId, string Code, string FirstName, string LastName,
    IReadOnlyList<string> Roles, bool IsActive, bool IsLockedOut);

/// <param name="Roles">Any of Cashier, Supervisor, Admin.</param>
public sealed record OperatorCreate(Guid CompanyId, Guid? SiteId, string Code, string FirstName, string LastName,
    IReadOnlyList<string> Roles, string Pin);

public sealed record OperatorUpdate(Guid? SiteId, string FirstName, string LastName, IReadOnlyList<string> Roles, bool IsActive);

public sealed record SetPinRequest(string Pin);

public sealed record AccessScopeDto(Guid Id, string UserName, Guid CompanyId, Guid? SiteId);

public sealed record AccessScopeCreate(string UserName, Guid CompanyId, Guid? SiteId);
