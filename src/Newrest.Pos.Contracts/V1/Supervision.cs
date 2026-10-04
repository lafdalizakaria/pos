namespace Newrest.Pos.Contracts.V1;

/// <param name="Severity">Info, Warning or Critical.</param>
public sealed record AlertDto(string Key, string Severity, string Code, Guid? RegisterId, string Subject, string Message, DateTimeOffset Since);

public sealed record RegisterSupervisionDto(Guid RegisterId, string Prefix, string Name, string SiteName, DateTimeOffset? LastSeenAt, string? AppVersion,
    int? PendingCount, string? BlockingError, DateTimeOffset? OpenSessionSince, DateTimeOffset? LastBackupAt, bool? IntegrityValid,
    DateTimeOffset? IntegrityCheckedAt, int? TicketsChecked, string? VisionProvider, string? VisionModelVersion);

public sealed record SupervisionDashboardDto(DateTimeOffset GeneratedAt, IReadOnlyList<AlertDto> Alerts, IReadOnlyList<RegisterSupervisionDto> Registers);

/// <summary>Account consumption recorded by a register whose ticket never reached the server (rare: lost response + offline refusal).</summary>
public sealed record DebitWithoutTicketDto(Guid MovementId, Guid AccountId, string DinerName, string RegisterPrefix, decimal Amount, DateTimeOffset OccurredAt,
    Guid TicketId);

public sealed record OfflineOverdraftDto(Guid MovementId, Guid AccountId, string DinerName, string RegisterPrefix, decimal Amount, decimal BalanceAfter,
    DateTimeOffset OccurredAt);

public sealed record LateTicketDto(Guid TicketId, string Number, DateTimeOffset IssuedAt, DateTimeOffset ReceivedAt);

public sealed record ReconciliationDto(DateOnly From, DateOnly To, IReadOnlyList<DebitWithoutTicketDto> DebitsWithoutTicket,
    IReadOnlyList<OfflineOverdraftDto> OfflineOverdrafts, IReadOnlyList<LateTicketDto> LateTickets);

public sealed record IntegrityRunDto(int RegistersChecked, int Invalid, DateTimeOffset CheckedAt);
