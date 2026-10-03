using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Receipts;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Devices.Display;
using Newrest.Pos.Devices.Printing;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Client.Core.Sales;

/// <summary>A payment chosen by the cashier. Account payments are authorised by <see cref="SaleService"/>.</summary>
public sealed record PaymentChoice(PaymentMethod Method, decimal Amount, decimal? Tendered = null, string? AuthorizationCode = null);

public sealed record SaleResult(Ticket Ticket, ReceiptDocument Receipt, bool AccountDebitedOffline, string? PrintError);

/// <summary>
/// Sale workflow: identify the diner, compute the subsidy, authorise the account payment (online when possible,
/// otherwise within the offline limit), then issue the fiscal ticket atomically (sequence + ticket + outbox).
/// </summary>
public sealed partial class SaleService(
    LocalStore store, PosApiClient api, ReferenceCache cache, SyncService sync, RegisterSetupService setup, RegisterOptions options,
    IReceiptPrinter printer, ICustomerDisplay display, ReceiptBuilder receipts, ConnectivityState connectivity, TimeProvider clock,
    ILogger<SaleService> logger)
{
    /// <summary>Badge lookup: online for the freshest balance and daily subsidy, otherwise from the offline cache.</summary>
    public async Task<DinerContext> IdentifyBadgeAsync(string number, DateOnly businessDate, CancellationToken ct = default)
    {
        var normalized = Badge(number);
        DinerContext context;
        try
        {
            var online = await api.GetBadgeContextAsync(normalized, businessDate, ct);
            connectivity.IsOnline = true;
            context = new DinerContext(online.Diner, online.Badge, online.Account, online.ContractAcceptsPointOfSale, online.SubsidyGrantedToday,
                online.SubsidizedMealsToday, online.SubsidyRules, IsOnline: true,
                online.Account is { } a ? a.Balance + a.OverdraftLimit : 0m);
        }
        catch (ServerRejectedException ex) when (ex.Code == "not_found")
        {
            throw new DomainException("badge_unknown", "Badge inconnu.");
        }
        catch (ServerUnreachableException)
        {
            connectivity.IsOnline = false;
            context = await IdentifyOfflineAsync(normalized, businessDate, ct)
                      ?? throw new DomainException("badge_unknown", "Badge inconnu (hors ligne).");
        }

        if (context.Badge.Status != "Active")
        {
            throw new DomainException("badge_refused", context.Badge.Status == "Lost" ? "Badge déclaré perdu." : "Badge bloqué.");
        }

        if (!context.Diner.IsActive)
        {
            throw new DomainException("badge_refused", "Convive inactif.");
        }

        return context;
    }

    private async Task<DinerContext?> IdentifyOfflineAsync(string number, DateOnly businessDate, CancellationToken ct)
    {
        var badge = await cache.FindBadgeAsync(number, ct);
        if (badge is null || await cache.GetDinerAsync(badge.DinerId, ct) is not { } diner)
        {
            return null;
        }

        SyncAccountDto? account = null;
        var accepted = false;
        foreach (var candidate in (await cache.GetAccountsOfDinerAsync(diner.Id, ct)).OrderByDescending(a => a.IsActive))
        {
            if (await cache.GetContractAsync(candidate.ContractId, ct) is { AcceptsThisPointOfSale: true, IsActive: true } contract
                && businessDate >= contract.StartDate && (contract.EndDate is null || businessDate <= contract.EndDate))
            {
                account = candidate;
                accepted = true;
                break;
            }
        }

        var rules = account is null ? [] : (await cache.GetRulesAsync(account.ContractId, ct)).ToList();
        await using var db = store.Open();
        var today = await db.Tickets.AsNoTracking()
            .Where(t => t.DinerId == diner.Id && t.BusinessDate == businessDate && t.SubsidyAmount != 0)
            .Select(t => new { t.SubsidyAmount, t.Kind }).ToListAsync(ct);
        var meals = today.Count(t => t.Kind == nameof(TicketKind.Sale)) - today.Count(t => t.Kind == nameof(TicketKind.CreditNote));
        var available = account is null ? 0m : account.Balance + account.OverdraftLimit + await UnsyncedBalanceChangeAsync(db, account.Id, ct);
        return new DinerContext(diner, badge, account, accepted, today.Sum(t => t.SubsidyAmount), Math.Max(0, meals), rules, IsOnline: false, available);
    }

    /// <summary>Local movements not yet reflected in the cached balance (offline debits, refunds, top-ups).</summary>
    private static async Task<decimal> UnsyncedBalanceChangeAsync(ClientDbContext db, Guid accountId, CancellationToken ct) =>
        (await db.AccountMovements.AsNoTracking().Where(m => m.AccountId == accountId && !m.Synced).Select(m => m.Amount).ToListAsync(ct)).Sum();

    private static async Task<decimal> PendingOfflineDebitsAsync(ClientDbContext db, Guid accountId, CancellationToken ct) =>
        -(await db.AccountMovements.AsNoTracking()
            .Where(m => m.AccountId == accountId && !m.ConfirmedOnline && !m.Synced && m.Amount < 0)
            .Select(m => m.Amount).ToListAsync(ct)).Sum();

    /// <summary>
    /// Issues the ticket. Steps: validate the payments (dry run), authorise the account payment (server ledger when
    /// online — balance enforced; offline limited by the per-badge cap), then in one SQLite transaction assign the next
    /// sequence, seal the ticket, store it and queue it after its offline movements.
    /// </summary>
    public async Task<SaleResult> CompleteSaleAsync(Cart cart, IReadOnlyList<PaymentChoice> payments, LoggedOperator cashier, LocalCashSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cart);
        ArgumentNullException.ThrowIfNull(payments);
        var profile = setup.Profile ?? throw new InvalidOperationException("Caisse non enregistrée.");
        if (cart.IsEmpty)
        {
            throw new DomainException("empty_ticket", "Le ticket est vide.");
        }

        var subsidy = cart.ComputeSubsidy(session.BusinessDate);
        var diner = cart.Diner;
        var accountPayment = payments.Where(p => p.Method == PaymentMethod.Account).Sum(p => p.Amount);
        if (accountPayment > 0 && diner?.CanPayWithAccount != true)
        {
            throw new DomainException("account_unavailable", "Paiement par compte impossible pour ce convive.");
        }

        var movementKey = accountPayment > 0 ? Guid.CreateVersion7() : (Guid?)null;
        var request = BuildRequest(cart, payments, cashier, session, profile, subsidy.EmployerShare, movementKey, sequence: 1, TicketHasher.GenesisHash);
        Ticket.Issue(request); // dry run: refuse inconsistent payments before touching the account

        var now = clock.GetUtcNow();
        var offline = false;
        decimal? balanceAfter = null;
        AccountMovementSyncDto? movement = null;
        if (movementKey is { } key)
        {
            movement = new AccountMovementSyncDto(key, diner!.Account!.Id, nameof(MovementType.Consumption), -accountPayment, now, null, cart.TicketId,
                cashier.Id, null, IsOfflineReplay: false);
            (offline, balanceAfter) = await AuthorizeDebitAsync(movement, diner, ct);
            if (offline)
            {
                movement = movement with { IsOfflineReplay = true };
            }
        }

        Ticket ticket;
        await using (var db = store.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var state = await db.RegisterStates.SingleAsync(ct);
            if (state.OpenSessionId != session.Id)
            {
                throw new DomainException("no_session", "La session de caisse n'est plus ouverte.");
            }

            ticket = Ticket.Issue(BuildRequest(cart, payments, cashier, session, profile, subsidy.EmployerShare, movementKey,
                TicketNumber.Next(state.LastSequence), state.LastHash) with
            { IssuedAt = now });
            var dto = ticket.ToSyncDto();
            if (movement is not null)
            {
                db.AccountMovements.Add(new LocalAccountMovement
                {
                    IdempotencyKey = movement.IdempotencyKey,
                    AccountId = movement.AccountId,
                    BadgeNumber = diner!.Badge.Number,
                    Type = movement.Type,
                    Amount = movement.Amount,
                    OccurredAt = now,
                    TicketId = ticket.Id,
                    CashSessionId = session.Id,
                    ConfirmedOnline = !offline,
                    Synced = !offline,
                });
                if (offline)
                {
                    Outbox.Enqueue(db, OutboxKind.AccountMovement, movement.IdempotencyKey, movement, now);
                }
            }

            db.Tickets.Add(ToLocal(ticket, dto));
            Outbox.Enqueue(db, OutboxKind.Ticket, ticket.Id, dto, now);
            state.LastSequence = ticket.Sequence;
            state.LastHash = ticket.Hash;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        sync.Trigger();
        var receipt = receipts.BuildTicket(ticket, profile, cashier.DisplayName, diner?.DisplayName, balanceAfter);
        var printError = await TryPrintAsync(receipt, ct);
        await display.ShowAsync(new CustomerDisplayState([], ticket.TotalAmount, ticket.SubsidyAmount, ticket.DinerShare, "Merci, bon appétit !"), ct);
        return new SaleResult(ticket, receipt, offline, printError);
    }

    /// <returns>Offline flag and, when online, the balance after the debit.</returns>
    private async Task<(bool Offline, decimal? BalanceAfter)> AuthorizeDebitAsync(AccountMovementSyncDto movement, DinerContext diner, CancellationToken ct)
    {
        try
        {
            var result = await api.PostAccountMovementAsync(movement, ct);
            connectivity.IsOnline = true;
            return (false, result.BalanceAfter);
        }
        catch (ServerRejectedException ex) when (ex.Code == "insufficient_funds")
        {
            throw new DomainException("insufficient_funds", "Solde insuffisant : choisir un autre moyen de paiement.");
        }
        catch (ServerUnreachableException)
        {
            connectivity.IsOnline = false;
        }

        // Offline: cached balance minus local pending operations, and at most the offline cap per badge on this register.
        await using var db = store.Open();
        var amount = -movement.Amount;
        var cached = diner.Account!.Balance + diner.Account.OverdraftLimit + await UnsyncedBalanceChangeAsync(db, movement.AccountId, ct);
        if (amount > cached)
        {
            throw new DomainException("insufficient_funds", "Solde insuffisant (hors ligne) : choisir un autre moyen de paiement.");
        }

        var pending = await PendingOfflineDebitsAsync(db, movement.AccountId, ct);
        if (pending + amount > options.OfflineSpendingLimitPerBadge)
        {
            LogOfflineCap(logger, diner.Badge.Number, pending, amount);
            throw new DomainException("offline_limit",
                $"Plafond hors ligne atteint ({options.OfflineSpendingLimitPerBadge:0.00} MAD) : choisir un autre moyen de paiement.");
        }

        return (true, null);
    }

    private TicketIssueRequest BuildRequest(Cart cart, IReadOnlyList<PaymentChoice> payments, LoggedOperator cashier, LocalCashSession session,
        RegisterProfileDto profile, decimal subsidy, Guid? movementKey, long sequence, string previousHash) => new()
        {
            Id = cart.TicketId,
            RegisterId = profile.RegisterId,
            RegisterPrefix = profile.TicketPrefix,
            Sequence = sequence,
            PreviousHash = previousHash,
            CashSessionId = session.Id,
            OperatorId = cashier.Id,
            BusinessDate = session.BusinessDate,
            IssuedAt = clock.GetUtcNow(),
            Lines = [.. cart.Lines.Where(l => l.Quantity > 0).Select(l => l.ToInput())],
            Payments = [.. payments.Where(p => p.Amount != 0).Select(p => new PaymentInput(p.Method, p.Amount)
            {
                Tendered = p.Tendered,
                AuthorizationCode = p.AuthorizationCode,
                AccountMovementId = p.Method == PaymentMethod.Account ? movementKey : null,
            })],
            SubsidyAmount = subsidy,
            SubsidyRuleId = subsidy > 0 ? cart.Diner?.SelectRule(session.BusinessDate)?.Id : null,
            DinerId = cart.Diner?.Diner.Id,
            AccountId = cart.Diner?.CanPayWithAccount == true ? cart.Diner.Account!.Id : null,
            BadgeNumber = cart.Diner?.Badge.Number,
        };

    internal static LocalTicket ToLocal(Ticket ticket, TicketSyncDto dto) => new()
    {
        Id = ticket.Id,
        Sequence = ticket.Sequence,
        Number = ticket.Number,
        Kind = ticket.Kind.ToString(),
        CashSessionId = ticket.CashSessionId,
        BusinessDate = ticket.BusinessDate,
        IssuedAt = ticket.IssuedAt,
        DinerId = ticket.DinerId,
        BadgeNumber = ticket.BadgeNumber,
        TotalAmount = ticket.TotalAmount,
        SubsidyAmount = ticket.SubsidyAmount,
        DinerShare = ticket.DinerShare,
        CreditedTicketId = ticket.CreditedTicketId,
        Hash = ticket.Hash,
        Json = JsonSerializer.Serialize(dto, LocalStore.Json),
    };

    internal async Task<string?> TryPrintAsync(ReceiptDocument receipt, CancellationToken ct)
    {
        try
        {
            await printer.PrintAsync(receipt, ct);
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            LogPrintFailed(logger, ex.Message);
            return "Impression impossible : le ticket est enregistré, réimprimez-le depuis l'historique.";
        }
    }

    private static string Badge(string number) => Newrest.Pos.Domain.Clients.Badge.NormalizeNumber(number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline cap reached for badge {Badge}: pending {Pending}, requested {Amount}")]
    private static partial void LogOfflineCap(ILogger logger, string badge, decimal pending, decimal amount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Receipt printing failed: {Reason}")]
    private static partial void LogPrintFailed(ILogger logger, string reason);
}
