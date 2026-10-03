using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Receipts;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Client.Core.Sales;

public sealed record TopUpResult(decimal Amount, decimal? BalanceAfter, bool Offline, string? PrintError);

/// <summary>Account top-ups at the register, credit notes (supervisor) and receipt reprints.</summary>
public sealed class AccountOperationsService(
    LocalStore store, PosApiClient api, SyncService sync, RegisterSetupService setup, SaleService sales, ReceiptBuilder receipts,
    ConnectivityState connectivity, TimeProvider clock)
{
    /// <summary>
    /// Top-up paid in cash or by card: a ledger movement, never a sale (no turnover, no VAT). Online it is posted at once;
    /// offline it is queued and the money is in the drawer (it appears in the Z either way).
    /// </summary>
    public async Task<TopUpResult> TopUpAsync(DinerContext diner, decimal amount, PaymentMethod method, LoggedOperator cashier, LocalCashSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diner);
        if (!diner.CanPayWithAccount)
        {
            throw new DomainException("account_unavailable", "Aucun compte utilisable pour ce convive sur ce point de vente.");
        }

        if (method is not (PaymentMethod.Cash or PaymentMethod.Card))
        {
            throw new DomainException("invalid_value", "Recharge en espèces ou par carte uniquement.");
        }

        Money.EnsureValid(amount, nameof(amount));
        if (amount <= 0)
        {
            throw new DomainException("invalid_amount", "Le montant doit être positif.");
        }

        var now = clock.GetUtcNow();
        var dto = new AccountMovementSyncDto(Guid.CreateVersion7(), diner.Account!.Id, nameof(MovementType.TopUp), amount, now, method.ToString(), null,
            cashier.Id, "Recharge caisse", IsOfflineReplay: false);
        decimal? balanceAfter = null;
        var offline = false;
        try
        {
            balanceAfter = (await api.PostAccountMovementAsync(dto, ct)).BalanceAfter;
            connectivity.IsOnline = true;
        }
        catch (ServerUnreachableException)
        {
            connectivity.IsOnline = false;
            offline = true;
            dto = dto with { IsOfflineReplay = true };
        }

        await using (var db = store.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.AccountMovements.Add(new LocalAccountMovement
            {
                IdempotencyKey = dto.IdempotencyKey, AccountId = dto.AccountId, BadgeNumber = diner.Badge.Number, Type = dto.Type, Amount = amount,
                OccurredAt = now, PaymentMethod = method.ToString(), CashSessionId = session.Id, ConfirmedOnline = !offline, Synced = !offline,
            });
            if (offline)
            {
                Outbox.Enqueue(db, OutboxKind.AccountMovement, dto.IdempotencyKey, dto, now);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        sync.Trigger();
        var printError = await sales.TryPrintAsync(
            receipts.BuildTopUp(setup.Profile!, cashier.DisplayName, diner.DisplayName, amount, method.ToString(), balanceAfter, now), ct);
        return new TopUpResult(amount, balanceAfter, offline, printError);
    }

    /// <summary>
    /// Full credit note of a ticket of this register (supervisor). Each payment is given back the same way; the account
    /// part is credited back on the ledger (online, or queued before the credit note).
    /// </summary>
    public async Task<SaleResult> IssueCreditNoteAsync(Guid originalTicketId, string reason, LoggedOperator supervisor, LocalCashSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        if (!supervisor.IsSupervisor)
        {
            throw new DomainException("forbidden", "Un avoir doit être validé par un responsable.");
        }

        var profile = setup.Profile!;
        Ticket original;
        await using (var db = store.Open())
        {
            var row = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == originalTicketId, ct)
                      ?? throw new DomainException("not_found", "Ticket introuvable sur cette caisse.");
            if (await db.Tickets.AnyAsync(t => t.CreditedTicketId == originalTicketId, ct))
            {
                throw new DomainException("already_credited", "Ce ticket a déjà fait l'objet d'un avoir.");
            }

            original = await LocalTickets.RebuildAsync(db, profile, row, ct);
        }

        if (original.Kind != TicketKind.Sale)
        {
            throw new DomainException("cannot_credit_credit_note", "Un avoir ne peut pas être annulé par un avoir.");
        }

        var now = clock.GetUtcNow();
        AccountMovementSyncDto? refund = null;
        var refunds = new List<PaymentInput>();
        foreach (var p in original.Payments)
        {
            if (p.Method == PaymentMethod.Account)
            {
                refund = new AccountMovementSyncDto(Guid.CreateVersion7(), original.AccountId!.Value, nameof(MovementType.Refund), p.Amount, now, null, null,
                    supervisor.Id, $"Avoir sur {original.Number}", IsOfflineReplay: false);
                refunds.Add(new PaymentInput(PaymentMethod.Account, -p.Amount) { AccountMovementId = refund.IdempotencyKey });
            }
            else
            {
                refunds.Add(new PaymentInput(p.Method, -p.Amount) { AuthorizationCode = p.AuthorizationCode });
            }
        }

        // Dry run before touching the ledger.
        var request = new CreditNoteRequest
        {
            Id = Guid.CreateVersion7(), RegisterPrefix = profile.TicketPrefix, Sequence = 1, PreviousHash = TicketHasher.GenesisHash,
            CashSessionId = session.Id, OperatorId = supervisor.Id, BusinessDate = session.BusinessDate, IssuedAt = now, Reason = reason,
            RefundPayments = refunds,
        };
        Ticket.IssueCreditNote(original, request);

        var offline = false;
        if (refund is not null)
        {
            refund = refund with { TicketId = request.Id };
            try
            {
                await api.PostAccountMovementAsync(refund, ct);
            }
            catch (ServerUnreachableException)
            {
                offline = true;
                refund = refund with { IsOfflineReplay = true };
            }
        }

        Ticket credit;
        await using (var db = store.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var state = await db.RegisterStates.SingleAsync(ct);
            credit = Ticket.IssueCreditNote(original, request with { Sequence = TicketNumber.Next(state.LastSequence), PreviousHash = state.LastHash });
            if (refund is not null)
            {
                db.AccountMovements.Add(new LocalAccountMovement
                {
                    IdempotencyKey = refund.IdempotencyKey, AccountId = refund.AccountId, Type = refund.Type, Amount = refund.Amount, OccurredAt = now,
                    TicketId = credit.Id, CashSessionId = session.Id, ConfirmedOnline = !offline, Synced = !offline,
                });
                if (offline)
                {
                    Outbox.Enqueue(db, OutboxKind.AccountMovement, refund.IdempotencyKey, refund, now);
                }
            }

            var dto = credit.ToSyncDto();
            db.Tickets.Add(SaleService.ToLocal(credit, dto));
            Outbox.Enqueue(db, OutboxKind.Ticket, credit.Id, dto, now);
            state.LastSequence = credit.Sequence;
            state.LastHash = credit.Hash;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        sync.Trigger();
        var receipt = receipts.BuildTicket(credit, profile, supervisor.DisplayName);
        return new SaleResult(credit, receipt, offline, await sales.TryPrintAsync(receipt, ct));
    }

    public async Task<IReadOnlyList<LocalTicket>> ListSessionTicketsAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = store.Open();
        return await db.Tickets.AsNoTracking().Where(t => t.CashSessionId == sessionId).OrderByDescending(t => t.Sequence).ToListAsync(ct);
    }

    public async Task<string?> ReprintAsync(Guid ticketId, string cashierName, CancellationToken ct = default)
    {
        await using var db = store.Open();
        var row = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, ct);
        var ticket = await LocalTickets.RebuildAsync(db, setup.Profile!, row, ct);
        var copy = receipts.BuildTicket(ticket, setup.Profile!, cashierName);
        var lines = new List<Devices.Printing.ReceiptLine> { new("*** DUPLICATA ***", Devices.Printing.ReceiptAlignment.Center, Bold: true) };
        lines.AddRange(copy.Lines);
        return await sales.TryPrintAsync(copy with { Lines = lines, OpenDrawer = false }, ct);
    }

    internal static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, LocalStore.Json)!;
}
