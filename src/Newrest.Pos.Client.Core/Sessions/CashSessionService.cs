using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Receipts;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Devices.Printing;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Client.Core.Sessions;

/// <summary>Opening (cash float) and closing (Z report) of the register shift.</summary>
public sealed class CashSessionService(
    LocalStore store, RegisterSetupService setup, SyncService sync, IReceiptPrinter printer, ReceiptBuilder receipts, TimeProvider clock)
{
    public async Task<LocalCashSession?> GetOpenSessionAsync(CancellationToken ct = default)
    {
        await using var db = store.Open();
        var state = await db.RegisterStates.AsNoTracking().SingleAsync(ct);
        return state.OpenSessionId is { } id ? await db.CashSessions.AsNoTracking().SingleAsync(s => s.Id == id, ct) : null;
    }

    /// <summary>Business date in the site's time zone.</summary>
    public DateOnly Today()
    {
        var zone = setup.Profile is { } p && TimeZoneInfo.TryFindSystemTimeZoneById(p.SiteTimeZone, out var tz) ? tz : TimeZoneInfo.Local;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }

    public async Task<LocalCashSession> OpenAsync(Guid operatorId, decimal openingFloat, CancellationToken ct = default)
    {
        Money.EnsureValid(Guard.NotNegative(openingFloat, nameof(openingFloat)), nameof(openingFloat));
        var now = clock.GetUtcNow();
        var session = new LocalCashSession
        {
            Id = Guid.CreateVersion7(),
            OperatorId = operatorId,
            OpenedAt = now,
            BusinessDate = Today(),
            OpeningFloat = openingFloat,
            Status = LocalSessionStatus.Open,
        };

        await using (var db = store.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var state = await db.RegisterStates.SingleAsync(ct);
            if (state.OpenSessionId is not null)
            {
                throw new DomainException("session_open", "Une session de caisse est déjà ouverte.");
            }

            db.CashSessions.Add(session);
            state.OpenSessionId = session.Id;
            Outbox.Enqueue(db, OutboxKind.CashSessionOpened, session.Id,
                new CashSessionSyncDto(session.Id, operatorId, now, session.BusinessDate, openingFloat), now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        sync.Trigger();
        return session;
    }

    /// <summary>Preview of the Z (expected cash) before the count is entered.</summary>
    public async Task<ZReport> PreviewAsync(decimal countedCash, CancellationToken ct = default)
    {
        await using var db = store.Open();
        var state = await db.RegisterStates.AsNoTracking().SingleAsync(ct);
        var session = await RequireOpenAsync(db, state, ct);
        return await ComputeAsync(db, session, state.LastZNumber + 1, countedCash, clock.GetUtcNow(), ct);
    }

    /// <summary>Closes the session: Z computed from local tickets, stored, queued, printed. <paramref name="forced"/> = supervisor closing.</summary>
    public async Task<ZReport> CloseAsync(Guid operatorId, decimal countedCash, bool forced = false, CancellationToken ct = default)
    {
        ZReport z;
        await using (var db = store.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var state = await db.RegisterStates.SingleAsync(ct);
            var session = await RequireOpenAsync(db, state, ct);
            var now = clock.GetUtcNow();
            z = await ComputeAsync(db, session, state.LastZNumber + 1, countedCash, now, ct);
            var dto = new ZReportSyncDto(z.Id, session.Id, z.ZNumber, now, countedCash, operatorId, forced, z.LastTicketSequence, z.NetSales, z.TotalVat,
                z.ExpectedCash);
            db.ZReports.Add(new LocalZReport
            {
                Id = z.Id,
                CashSessionId = session.Id,
                ZNumber = z.ZNumber,
                GeneratedAt = now,
                Json = JsonSerializer.Serialize(dto, LocalStore.Json)
            });
            var tracked = await db.CashSessions.SingleAsync(s => s.Id == session.Id, ct);
            tracked.Status = LocalSessionStatus.Closed;
            tracked.ClosedAt = now;
            tracked.ZReportId = z.Id;
            state.LastZNumber = z.ZNumber;
            state.OpenSessionId = null;
            Outbox.Enqueue(db, OutboxKind.ZReport, z.Id, dto, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        sync.Trigger();
        try
        {
            await printer.PrintAsync(receipts.BuildZ(z, setup.Profile!), ct);
        }
        catch (IOException)
        {
            // The Z is stored and can be reprinted from history; closing must not fail on a paper jam.
        }

        return z;
    }

    private static async Task<LocalCashSession> RequireOpenAsync(ClientDbContext db, RegisterState state, CancellationToken ct) =>
        state.OpenSessionId is { } id
            ? await db.CashSessions.AsNoTracking().SingleAsync(s => s.Id == id, ct)
            : throw new DomainException("no_session", "Aucune session de caisse ouverte.");

    private async Task<ZReport> ComputeAsync(ClientDbContext db, LocalCashSession session, int zNumber, decimal counted, DateTimeOffset now,
        CancellationToken ct)
    {
        var profile = setup.Profile ?? throw new InvalidOperationException("Register not registered.");
        var tickets = await LocalTickets.LoadDomainTicketsAsync(db, profile, session.Id, ct);
        var collections = await db.AccountMovements.AsNoTracking()
            .Where(m => m.CashSessionId == session.Id && m.PaymentMethod != null)
            .Select(m => new { m.PaymentMethod, m.Amount }).ToListAsync(ct);
        var domainSession = new CashSession(session.Id, profile.RegisterId, session.OperatorId, session.OpeningFloat, session.OpenedAt, session.BusinessDate);
        return ZReportCalculator.Compute(new ZReportRequest(Guid.CreateVersion7(), domainSession, zNumber, now, counted, tickets,
            [.. collections.Select(c => new RegisterCollection(Enum.Parse<PaymentMethod>(c.PaymentMethod!), c.Amount))]));
    }
}

/// <summary>Rebuilds domain tickets from the local store (same code as the server, so hashes are re-verified).</summary>
public static class LocalTickets
{
    public static async Task<List<Ticket>> LoadDomainTicketsAsync(ClientDbContext db, RegisterProfileDto profile, Guid sessionId, CancellationToken ct)
    {
        var rows = await db.Tickets.AsNoTracking().Where(t => t.CashSessionId == sessionId).OrderBy(t => t.Sequence).ToListAsync(ct);
        var result = new List<Ticket>();
        foreach (var row in rows)
        {
            result.Add(await RebuildAsync(db, profile, row, ct));
        }

        return result;
    }

    public static async Task<Ticket> RebuildAsync(ClientDbContext db, RegisterProfileDto profile, LocalTicket row, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<TicketSyncDto>(row.Json, LocalStore.Json)!;
        if (dto.Kind != nameof(TicketKind.CreditNote))
        {
            return dto.RebuildSale(profile.RegisterId, profile.TicketPrefix);
        }

        var original = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == dto.CreditedTicketId, ct);
        return dto.RebuildCreditNote(await RebuildAsync(db, profile, original, ct), profile.TicketPrefix);
    }
}
