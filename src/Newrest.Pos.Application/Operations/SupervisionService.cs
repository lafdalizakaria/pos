using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Operations;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Application.Operations;

/// <summary>
/// Operations supervision: register heartbeats, alerts, nightly verification of every ticket chain and the
/// reconciliation of account debits with tickets. Methods suffixed <c>System</c> run in background jobs (no user).
/// </summary>
public sealed class SupervisionService(IPosDbContext db, AccessControl access, TimeProvider clock, SupervisionThresholds thresholds)
{
    private const int IntegrityBatch = 2000;

    public async Task RecordHeartbeatAsync(Guid registerId, RegisterHeartbeatDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var register = await db.Registers.SingleOrDefaultAsync(r => r.Id == registerId && r.IsActive, ct)
                       ?? throw new ForbiddenException("Unknown or inactive register.");
        var heartbeat = await db.RegisterHeartbeats.SingleOrDefaultAsync(h => h.Id == registerId, ct);
        if (heartbeat is null)
        {
            heartbeat = new RegisterHeartbeat(registerId);
            db.RegisterHeartbeats.Add(heartbeat);
        }

        var now = clock.GetUtcNow();
        heartbeat.Record(dto.AppVersion, dto.PendingCount, dto.OldestPendingAt, dto.BlockingError, dto.LocalLastSequence, dto.OpenSessionSince,
            dto.LastBackupAt, now);
        register.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        PosMetrics.Heartbeats.Add(1);
    }

    public async Task<SupervisionDashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        var (snapshot, registers) = await SnapshotAsync(scope.CanAccessSite, ct);
        var alerts = SupervisionRules.Evaluate(snapshot, thresholds, clock.GetUtcNow());
        return new SupervisionDashboardDto(clock.GetUtcNow(), [.. alerts.Select(ToDto)], registers);
    }

    /// <summary>All alerts, every site (background notification job).</summary>
    public async Task<IReadOnlyList<Alert>> EvaluateSystemAsync(CancellationToken ct = default)
    {
        var (snapshot, _) = await SnapshotAsync((_, _) => true, ct);
        var alerts = SupervisionRules.Evaluate(snapshot, thresholds, clock.GetUtcNow());
        PosMetrics.SetAlerts(alerts.Select(a => (a.Severity.ToString(), a.Code)));
        return alerts;
    }

    public async Task<IntegrityRunDto> RunIntegrityChecksAsync(CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin, PosRoles.Accountant);
        return await RunIntegrityChecksSystemAsync(ct);
    }

    /// <summary>Recomputes every ticket hash of every active register, in batches (memory-bounded), and stores the results.</summary>
    public async Task<IntegrityRunDto> RunIntegrityChecksSystemAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var registers = await db.Registers.AsNoTracking().Where(r => r.IsActive).Select(r => r.Id).ToListAsync(ct);
        var invalid = 0;
        foreach (var registerId in registers)
        {
            long next = 1;
            string? previousHash = null;
            var checkedCount = 0;
            var issues = new List<ChainIssue>();
            while (true)
            {
                var from = next;
                var batch = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments)
                    .Where(t => t.RegisterId == registerId && t.Sequence >= from).OrderBy(t => t.Sequence).Take(IntegrityBatch).ToListAsync(ct);
                if (batch.Count == 0)
                {
                    break;
                }

                var result = TicketChainVerifier.Verify(registerId, batch, next, previousHash);
                checkedCount += result.TicketsChecked;
                issues.AddRange(result.Issues);
                next = batch[^1].Sequence + 1;
                previousHash = batch[^1].Hash;
                if (batch.Count < IntegrityBatch)
                {
                    break;
                }
            }

            if (checkedCount == 0)
            {
                continue;
            }

            var summary = string.Join(" ; ", issues.Take(10).Select(i => $"n° {i.Sequence} : {IssueLabel(i.Kind)}"));
            db.IntegrityChecks.Add(new IntegrityCheck(registerId, now, checkedCount, issues.Count == 0, summary));
            invalid += issues.Count == 0 ? 0 : 1;
        }

        // Keep 90 days of history.
        await db.IntegrityChecks.Where(c => c.CheckedAt < now.AddDays(-90)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return new IntegrityRunDto(registers.Count, invalid, now);
    }

    public async Task<ReconciliationDto> GetReconciliationAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin, PosRoles.Accountant, PosRoles.Manager);
        var scope = await access.GetScopeAsync(ct);
        var registers = await RegistersAsync(scope.CanAccessSite, ct);
        var ids = registers.Keys.ToList();
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var withoutTicket = await DebitsWithoutTicketQuery(ids).Where(m => m.OccurredAt >= start && m.OccurredAt < end).ToListAsync(ct);
        var overdrafts = await db.AccountMovements.AsNoTracking()
            .Where(m => m.ExceededOverdraft && m.RegisterId != null && ids.Contains(m.RegisterId.Value) && m.OccurredAt >= start && m.OccurredAt < end)
            .OrderByDescending(m => m.OccurredAt).Take(500).ToListAsync(ct);
        var late = await db.Tickets.AsNoTracking()
            .Where(t => ids.Contains(t.RegisterId) && t.IssuedAt >= start && t.IssuedAt < end && t.ReceivedAt > t.IssuedAt.AddHours(24))
            .OrderByDescending(t => t.IssuedAt).Take(500).Select(t => new LateTicketDto(t.Id, t.Number, t.IssuedAt, t.ReceivedAt)).ToListAsync(ct);

        var accountIds = withoutTicket.Concat(overdrafts).Select(m => m.AccountId).Distinct().ToList();
        var names = await (from a in db.Accounts.AsNoTracking()
                           join d in db.Diners.AsNoTracking() on a.DinerId equals d.Id
                           where accountIds.Contains(a.Id)
                           select new { a.Id, Name = d.FirstName + " " + d.LastName }).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return new ReconciliationDto(from, to,
            [.. withoutTicket.Select(m => new DebitWithoutTicketDto(m.Id, m.AccountId, names.GetValueOrDefault(m.AccountId) ?? "?", registers[m.RegisterId!.Value].Prefix,
                -m.Amount, m.OccurredAt, m.TicketId!.Value))],
            [.. overdrafts.Select(m => new OfflineOverdraftDto(m.Id, m.AccountId, names.GetValueOrDefault(m.AccountId) ?? "?", registers[m.RegisterId!.Value].Prefix,
                -m.Amount, m.BalanceAfter, m.OccurredAt))],
            late);
    }

    private IQueryable<AccountMovement> DebitsWithoutTicketQuery(List<Guid> registerIds) =>
        db.AccountMovements.AsNoTracking()
            .Where(m => m.Type == MovementType.Consumption && m.TicketId != null && m.RegisterId != null && registerIds.Contains(m.RegisterId.Value))
            .Where(m => !db.Tickets.Any(t => t.Id == m.TicketId))
            .Where(m => !db.AccountMovements.Any(r => r.ReversesMovementId == m.Id));

    private sealed record RegisterInfo(Guid Id, string Prefix, string Name, string SiteName, DateTimeOffset? LastSeenAt);

    private async Task<Dictionary<Guid, RegisterInfo>> RegistersAsync(Func<Guid, Guid, bool> canAccess, CancellationToken ct) =>
        (await (from r in db.Registers.AsNoTracking()
                join p in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals p.Id
                join s in db.Sites.AsNoTracking() on p.SiteId equals s.Id
                where r.IsActive
                select new { r.Id, r.TicketPrefix, r.Name, SiteId = s.Id, SiteName = s.Name, s.CompanyId, r.LastSeenAt }).ToListAsync(ct))
        .Where(r => canAccess(r.CompanyId, r.SiteId))
        .ToDictionary(r => r.Id, r => new RegisterInfo(r.Id, r.TicketPrefix, r.Name, r.SiteName, r.LastSeenAt));

    private async Task<(SupervisionSnapshot Snapshot, IReadOnlyList<RegisterSupervisionDto> Rows)> SnapshotAsync(Func<Guid, Guid, bool> canAccess,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var registers = await RegistersAsync(canAccess, ct);
        var ids = registers.Keys.ToList();
        var heartbeats = await db.RegisterHeartbeats.AsNoTracking().Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var sessions = await db.CashSessions.AsNoTracking().Where(s => ids.Contains(s.RegisterId) && s.Status == CashSessionStatus.Open)
            .GroupBy(s => s.RegisterId).Select(g => new { g.Key, OpenedAt = g.Min(s => s.OpenedAt) }).ToDictionaryAsync(x => x.Key, x => x.OpenedAt, ct);
        var latest = await db.IntegrityChecks.AsNoTracking().Where(c => ids.Contains(c.RegisterId))
            .GroupBy(c => c.RegisterId).Select(g => g.OrderByDescending(c => c.CheckedAt).First()).ToListAsync(ct);
        var integrity = latest.ToDictionary(c => c.RegisterId);
        var withTickets = (await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.RegisterId)).Select(t => t.RegisterId).Distinct().ToListAsync(ct)).ToHashSet();
        var vision = await db.RegisterVisionStatuses.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var debits = await DebitsWithoutTicketQuery(ids).Where(m => m.OccurredAt >= now.AddDays(-30))
            .Select(m => new { m.Id, m.RegisterId, m.Amount, m.OccurredAt }).ToListAsync(ct);
        var overdrafts = await db.AccountMovements.AsNoTracking()
            .CountAsync(m => m.ExceededOverdraft && m.RegisterId != null && ids.Contains(m.RegisterId.Value) && m.OccurredAt >= now.AddDays(-7), ct);

        var snapshots = registers.Values.Select(r =>
        {
            var v = vision.GetValueOrDefault(r.Id);
            return new RegisterSnapshot(r.Id, r.Prefix, r.SiteName, r.LastSeenAt, heartbeats.GetValueOrDefault(r.Id),
                sessions.TryGetValue(r.Id, out var open) ? open : null, integrity.GetValueOrDefault(r.Id), withTickets.Contains(r.Id),
                v is null ? null : v.Error is null && v.ProviderReady, v?.Error, v?.ReportedAt);
        }).OrderBy(r => r.Prefix).ToList();
        var snapshot = new SupervisionSnapshot(snapshots,
            [.. debits.Select(d => (d.Id, registers[d.RegisterId!.Value].Prefix, -d.Amount, d.OccurredAt))], overdrafts);
        var rows = snapshots.Select(s =>
        {
            var r = registers[s.RegisterId];
            var v = vision.GetValueOrDefault(s.RegisterId);
            return new RegisterSupervisionDto(s.RegisterId, r.Prefix, r.Name, r.SiteName, s.Heartbeat?.ReportedAt > r.LastSeenAt ? s.Heartbeat.ReportedAt : r.LastSeenAt,
                s.Heartbeat?.AppVersion, s.Heartbeat?.PendingCount, s.Heartbeat?.BlockingError, s.OpenSessionSince, s.Heartbeat?.LastBackupAt,
                s.LatestIntegrity?.IsValid, s.LatestIntegrity?.CheckedAt, s.LatestIntegrity?.TicketsChecked, v?.Provider, v?.ModelVersion);
        }).ToList();
        return (snapshot, rows);
    }

    private static AlertDto ToDto(Alert a) => new(a.Key, a.Severity.ToString(), a.Code, a.RegisterId, a.Subject, a.Message, a.Since);

    private static string IssueLabel(ChainIssueKind kind) => kind switch
    {
        ChainIssueKind.Gap => "ticket manquant",
        ChainIssueKind.Duplicate => "numéro en double",
        ChainIssueKind.BrokenLink => "chaînage rompu",
        ChainIssueKind.HashMismatch => "contenu modifié",
        ChainIssueKind.ForeignRegister => "ticket d'une autre caisse",
        _ => kind.ToString(),
    };
}
