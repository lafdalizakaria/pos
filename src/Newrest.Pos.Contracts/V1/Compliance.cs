namespace Newrest.Pos.Contracts.V1;

public sealed record ArchiveDto(Guid Id, Guid CompanyId, string CompanyName, int Year, int Month, int TicketCount, long SizeBytes, string Sha256, string KeyId,
    DateTimeOffset CreatedAt, string CreatedBy);

public sealed record ArchiveCreate(Guid CompanyId, int Year, int Month);

public sealed record ArchiveVerificationDto(bool IsValid, string? KeyId, int Tickets, int ZReports, int Movements, IReadOnlyList<string> Issues);

/// <param name="DryRun">True: count only, nothing is changed.</param>
public sealed record AnonymizationRequest(DateOnly InactiveBefore, bool DryRun);

public sealed record AnonymizationResult(int Diners, int Badges, bool DryRun);
