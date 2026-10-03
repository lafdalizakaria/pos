using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Operations;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Application.Compliance;

/// <summary>
/// Monthly signed archives of a company's fiscal data, for the legal retention period (to be stored on immutable /
/// WORM storage). Each archive continues every register's chain exactly where the previous one stopped, so that the
/// whole history can be verified offline, archive by archive, without the database.
/// </summary>
public sealed class ArchiveService(IPosDbContext db, AccessControl access, AuditTrail audit, IFileStorage storage, IArchiveSigner signer, ICurrentUser user,
    TimeProvider clock)
{
    /// <summary>A month can be archived from this day of the following month (registers offline at month end have synchronised).</summary>
    public const int GraceDays = 3;

    public async Task<IReadOnlyList<ArchiveDto>> ListAsync(CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin, PosRoles.Accountant);
        var scope = await access.GetScopeAsync(ct);
        var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var archives = await db.ArchiveRecords.AsNoTracking().OrderByDescending(a => a.Year).ThenByDescending(a => a.Month).ToListAsync(ct);
        return [.. archives.Where(a => scope.CanReadCompany(a.CompanyId)).Select(a => ToDto(a, companies.GetValueOrDefault(a.CompanyId) ?? "?"))];
    }

    public async Task<ArchiveDto> CreateAsync(ArchiveCreate request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await access.EnsureCanManageCompanyAsync(request.CompanyId, ct, PosRoles.Admin, PosRoles.Accountant);
        if (request.Month is < 1 or > 12)
        {
            throw new DomainException("invalid_period", "Invalid month.");
        }

        var periodEnd = new DateOnly(request.Year, request.Month, 1).AddMonths(1);
        var now = clock.GetUtcNow();
        if (DateOnly.FromDateTime(now.UtcDateTime) < periodEnd.AddDays(GraceDays - 1))
        {
            throw new DomainException("period_not_closed", $"The month can be archived from {periodEnd.AddDays(GraceDays - 1):yyyy-MM-dd}.");
        }

        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == request.CompanyId, ct);
        var latest = await db.ArchiveRecords.AsNoTracking().Where(a => a.CompanyId == company.Id)
            .OrderByDescending(a => a.Year).ThenByDescending(a => a.Month).FirstOrDefaultAsync(ct);
        if (latest is not null && (latest.Year * 12 + latest.Month) >= request.Year * 12 + request.Month)
        {
            throw new ConflictException($"{latest.Year}-{latest.Month:00} is already archived: archives are made in chronological order.");
        }

        var previous = latest is null ? [] : JsonSerializer.Deserialize<List<RegisterCheckpoint>>(latest.CheckpointsJson, ArchiveFormat.Json)!;
        var registers = await (from r in db.Registers.AsNoTracking()
                               join p in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals p.Id
                               join s in db.Sites.AsNoTracking() on p.SiteId equals s.Id
                               where s.CompanyId == company.Id
                               orderby r.TicketPrefix
                               select new { r.Id, r.TicketPrefix }).ToListAsync(ct);

        var tickets = new List<TicketSyncDto>();
        var references = new List<TicketSyncDto>();
        var zReports = new List<object>();
        var checkpoints = new List<RegisterCheckpoint>();
        foreach (var register in registers)
        {
            var before = previous.FirstOrDefault(p => p.RegisterId == register.Id);
            var lastSequence = before?.LastSequence ?? 0;
            var lastHash = before?.LastHash ?? TicketHasher.GenesisHash;
            var lastZ = before?.LastZNumber ?? 0;
            var own = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments)
                .Where(t => t.RegisterId == register.Id && t.Sequence > lastSequence && t.BusinessDate < periodEnd)
                .OrderBy(t => t.Sequence).ToListAsync(ct);
            var chain = TicketChainVerifier.Verify(register.Id, own, lastSequence + 1, lastHash);
            if (!chain.IsValid)
            {
                throw new DomainException("integrity_failed",
                    $"Register {register.TicketPrefix}: the ticket chain is not intact ({chain.Issues.Count} issue(s)); the month cannot be sealed.");
            }

            var zs = await db.ZReports.AsNoTracking().Include(z => z.Lines)
                .Where(z => z.RegisterId == register.Id && z.ZNumber > lastZ && z.BusinessDate < periodEnd).OrderBy(z => z.ZNumber).ToListAsync(ct);
            var originals = own.Where(t => t.CreditedTicketId is { } id && own.All(o => o.Id != id)).Select(t => t.CreditedTicketId!.Value).ToList();
            references.AddRange((await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments).Where(t => originals.Contains(t.Id))
                .ToListAsync(ct)).Select(t => t.ToSyncDto()));
            tickets.AddRange(own.Select(t => t.ToSyncDto()));
            zReports.AddRange(zs.Select(z => new
            {
                z.Id,
                z.RegisterId,
                z.CashSessionId,
                z.ZNumber,
                z.BusinessDate,
                z.OpenedAt,
                z.GeneratedAt,
                z.FirstTicketSequence,
                z.LastTicketSequence,
                z.SaleCount,
                z.CreditNoteCount,
                z.GrossSales,
                z.CreditNotesTotal,
                z.NetSales,
                z.TotalVat,
                z.SubsidyTotal,
                z.DinerShareTotal,
                z.AccountTopUpTotal,
                z.OpeningFloat,
                z.ExpectedCash,
                z.CountedCash,
                z.CashDifference,
                z.LastTicketHash,
                Lines = z.Lines.Select(l => new { Section = l.Section.ToString(), l.Key, l.Count, l.Amount, l.BaseAmount, l.TaxAmount }),
            }));
            checkpoints.Add(new RegisterCheckpoint(register.Id, register.TicketPrefix, lastSequence + 1, own.Count > 0 ? own[^1].Sequence : lastSequence,
                lastHash, own.Count > 0 ? own[^1].Hash : lastHash, own.Count, zs.Count > 0 ? zs[^1].ZNumber : lastZ));
        }

        var since = latest is null ? DateTimeOffset.MinValue : new DateTimeOffset(new DateOnly(latest.Year, latest.Month, 1).AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var until = new DateTimeOffset(periodEnd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var movements = await (from m in db.AccountMovements.AsNoTracking()
                               join a in db.Accounts.AsNoTracking() on m.AccountId equals a.Id
                               join c in db.Contracts.AsNoTracking() on a.ContractId equals c.Id
                               join cc in db.ClientCompanies.AsNoTracking() on c.ClientCompanyId equals cc.Id
                               where cc.CompanyId == company.Id && m.OccurredAt >= since && m.OccurredAt < until
                               orderby m.OccurredAt
                               select new
                               {
                                   m.Id,
                                   m.AccountId,
                                   Type = m.Type.ToString(),
                                   m.Amount,
                                   m.OccurredAt,
                                   m.RecordedAt,
                                   m.IdempotencyKey,
                                   m.ReversesMovementId,
                                   m.TicketId,
                                   m.RegisterId,
                                   m.PerformedBy,
                                   m.Comment,
                                   m.BalanceAfter,
                                   m.IsOfflineReplay,
                                   m.ExceededOverdraft,
                               }).ToListAsync(ct);

        var files = new Dictionary<string, (byte[] Data, int Records)>
        {
            ["tickets.jsonl"] = (ArchiveFormat.JsonLines(tickets), tickets.Count),
            ["references.jsonl"] = (ArchiveFormat.JsonLines(references), references.Count),
            ["z-reports.jsonl"] = (ArchiveFormat.JsonLines(zReports), zReports.Count),
            ["account-movements.jsonl"] = (ArchiveFormat.JsonLines(movements), movements.Count),
        };
        var period = $"{request.Year}-{request.Month:00}";
        var manifest = new ArchiveManifest(ArchiveFormat.Version, company.Id, company.Code, company.LegalName, period, now, user.Name, signer.KeyId,
            [.. files.Select(f => new ArchiveFileEntry(f.Key, ArchiveFormat.Sha256(f.Value.Data), f.Value.Records))], checkpoints);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(ArchiveFormat.Json) { WriteIndented = true });

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                stream.Write(data);
            }

            Add("manifest.json", manifestBytes);
            Add("manifest.sig", Encoding.ASCII.GetBytes(Convert.ToBase64String(signer.Sign(manifestBytes))));
            Add("signing-key.pem", Encoding.ASCII.GetBytes(signer.PublicKeyPem));
            foreach (var (name, (data, _)) in files)
            {
                Add(name, data);
            }

            Add("README.txt", Encoding.UTF8.GetBytes(
                $"Archive fiscale {company.LegalName} — {period}\nVérification : Newrest.Pos.Migrator --verify-archive <fichier.zip> [--previous <archive précédente>]\n"
                + $"Clé de signature : {signer.KeyId} (ECDSA P-256, SHA-256). Conserver sur un stockage immuable (WORM).\n"));
        }

        var bytes = buffer.ToArray();
        var key = $"archives/{company.Code}/{period}.zip";
        using (var content = new MemoryStream(bytes))
        {
            await storage.SaveAsync(key, content, ct);
        }

        var record = new ArchiveRecord(Guid.CreateVersion7(), company.Id, request.Year, request.Month, key, ArchiveFormat.Sha256(bytes), bytes.Length,
            signer.KeyId, tickets.Count, JsonSerializer.Serialize(checkpoints, ArchiveFormat.Json), now, user.Name);
        db.ArchiveRecords.Add(record);
        audit.Record(AuditActions.Created, nameof(ArchiveRecord), record.Id,
            after: new { Company = company.Code, Period = period, record.Sha256, Tickets = tickets.Count, signer.KeyId }, companyId: company.Id);
        await db.SaveChangesAsync(ct);
        return ToDto(record, company.Name);
    }

    public async Task<(Stream Content, string FileName)?> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var record = await LoadAsync(id, ct);
        var company = await db.Companies.AsNoTracking().Where(c => c.Id == record.CompanyId).Select(c => c.Code).SingleAsync(ct);
        return await storage.OpenReadAsync(record.StoragePath, ct) is { } stream ? (stream, $"archive-{company}-{record.Year}-{record.Month:00}.zip") : null;
    }

    /// <summary>Re-verifies a stored archive: file hash, signature, tickets, chains and continuity with the previous archive.</summary>
    public async Task<ArchiveVerificationDto> VerifyAsync(Guid id, CancellationToken ct = default)
    {
        var record = await LoadAsync(id, ct);
        await using var stream = await storage.OpenReadAsync(record.StoragePath, ct)
                                 ?? throw new DomainException("archive_missing", "The archive file is missing from the storage.");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        var issues = new List<string>();
        if (ArchiveFormat.Sha256(copy.ToArray()) != record.Sha256)
        {
            issues.Add("Le fichier stocké ne correspond plus à l'empreinte enregistrée à sa création.");
        }

        var previous = await db.ArchiveRecords.AsNoTracking()
            .Where(a => a.CompanyId == record.CompanyId && a.Year * 12 + a.Month < record.Year * 12 + record.Month)
            .OrderByDescending(a => a.Year).ThenByDescending(a => a.Month).FirstOrDefaultAsync(ct);
        copy.Position = 0;
        var result = ArchiveFormat.Verify(copy, record.KeyId,
            previous is null ? null : JsonSerializer.Deserialize<List<RegisterCheckpoint>>(previous.CheckpointsJson, ArchiveFormat.Json));
        return result with { IsValid = result.IsValid && issues.Count == 0, Issues = [.. issues, .. result.Issues] };
    }

    private async Task<ArchiveRecord> LoadAsync(Guid id, CancellationToken ct)
    {
        access.RequireAnyRole(PosRoles.Admin, PosRoles.Accountant);
        var record = await db.ArchiveRecords.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Archive", id);
        await access.EnsureCanReadCompanyAsync(record.CompanyId, ct);
        return record;
    }

    private static ArchiveDto ToDto(ArchiveRecord a, string companyName) =>
        new(a.Id, a.CompanyId, companyName, a.Year, a.Month, a.TicketCount, a.SizeBytes, a.Sha256, a.KeyId, a.CreatedAt, a.CreatedBy);
}
