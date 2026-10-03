using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Operations;

/// <summary>
/// A signed monthly archive of a company's fiscal data (tickets, Z reports, ledger). Immutable: a month is archived once;
/// late tickets are included in the next archive, which continues every register's chain where this one stopped.
/// </summary>
public sealed class ArchiveRecord : Entity, IImmutableRecord
{
    private ArchiveRecord()
    {
    }

    public ArchiveRecord(Guid id, Guid companyId, int year, int month, string storagePath, string sha256, long sizeBytes, string keyId, int ticketCount,
        string checkpointsJson, DateTimeOffset createdAt, string createdBy) : base(id)
    {
        CompanyId = Guard.NotEmpty(companyId, nameof(companyId));
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            throw new DomainException("invalid_period", "Invalid archive period.");
        }

        Year = year;
        Month = month;
        StoragePath = Guard.NotBlank(storagePath, nameof(storagePath), 500);
        Sha256 = Guard.NotBlank(sha256, nameof(sha256), 64);
        SizeBytes = sizeBytes;
        KeyId = Guard.NotBlank(keyId, nameof(keyId), 64);
        TicketCount = ticketCount;
        CheckpointsJson = checkpointsJson;
        CreatedAt = createdAt;
        CreatedBy = Guard.NotBlank(createdBy, nameof(createdBy));
    }

    public Guid CompanyId { get; private set; }
    public int Year { get; private set; }
    public int Month { get; private set; }
    public string StoragePath { get; private set; } = null!;
    public string Sha256 { get; private set; } = null!;
    public long SizeBytes { get; private set; }

    /// <summary>Identifier of the signing key (SHA-256 of its public key, 16 hex characters).</summary>
    public string KeyId { get; private set; } = null!;

    public int TicketCount { get; private set; }

    /// <summary>Per register: last archived sequence, hash and Z number (where the next archive continues).</summary>
    public string CheckpointsJson { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = null!;
}
