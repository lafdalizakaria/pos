namespace Newrest.Pos.Contracts.V1;

public sealed record RegisterTokenRequest(Guid RegisterId, string DeviceKey);

public sealed record RegisterTokenResponse(string AccessToken, DateTimeOffset ExpiresAt, Guid RegisterId, Guid PointOfSaleId,
    Guid SiteId, Guid CompanyId, string TicketPrefix);

public sealed record AuditEntryDto(Guid Id, DateTimeOffset OccurredAt, string Actor, string Action, string EntityType,
    string? EntityId, string? BeforeJson, string? AfterJson, Guid? CompanyId, Guid? SiteId, string? CorrelationId);
