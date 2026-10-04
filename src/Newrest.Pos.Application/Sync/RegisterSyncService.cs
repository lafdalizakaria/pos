using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Accounts;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Application.Sync;

/// <summary>
/// Ingestion of what registers produce (cash sessions, account movements, tickets, Z reports). Every operation is
/// idempotent on the client-generated identifier, so an outbox can safely replay after a lost response.
/// Tickets must arrive in sequence order and chain on the previous ticket of the register; the server rebuilds each
/// ticket with the domain code and refuses it if its hash differs.
/// </summary>
public sealed partial class RegisterSyncService(IPosDbContext db, IAccountLedger ledger, TimeProvider clock, ILogger<RegisterSyncService> logger)
{
    public async Task<SyncAck> OpenCashSessionAsync(Guid registerId, CashSessionSyncDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var existing = await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == dto.Id, ct);
        if (existing is not null)
        {
            return existing.RegisterId == registerId
                ? new SyncAck(dto.Id, WasDuplicate: true)
                : throw new ConflictException("The cash session belongs to another register.");
        }

        await EnsureOperatorAsync(registerId, dto.OperatorId, ct);
        if (await db.CashSessions.AnyAsync(s => s.RegisterId == registerId && s.Status == CashSessionStatus.Open, ct))
        {
            throw new DomainException("session_already_open", "The previous cash session of this register has not been closed (Z report missing).");
        }

        db.CashSessions.Add(new CashSession(dto.Id, registerId, dto.OperatorId, dto.OpeningFloat, dto.OpenedAt, dto.BusinessDate));
        await TouchRegisterAsync(registerId, ct);
        await db.SaveChangesAsync(ct);
        return new SyncAck(dto.Id, WasDuplicate: false);
    }

    /// <summary>
    /// Account movement made at the register. Online (<see cref="AccountMovementSyncDto.IsOfflineReplay"/> false) the
    /// balance + overdraft limit is enforced; an offline replay is recorded even beyond it and flagged.
    /// </summary>
    public async Task<LedgerResultDto> PostAccountMovementAsync(Guid registerId, AccountMovementSyncDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var type = Mappings.Parse<MovementType>(dto.Type, "movement type");
        if (type is not (MovementType.Consumption or MovementType.TopUp or MovementType.Refund))
        {
            throw new ForbiddenException("Registers may only post consumptions, top-ups and credit-note refunds.");
        }

        var scope = await new RegisterReferenceService(db, NoRowVersions.Instance, clock).LoadScopeAsync(registerId, ct);
        var acceptedHere = await (from a in db.Accounts
                                  join c in db.Contracts on a.ContractId equals c.Id
                                  where a.Id == dto.AccountId && c.PointsOfSale.Any(p => p.PointOfSaleId == scope.PointOfSaleId)
                                  select a.Id).AnyAsync(ct);
        if (!acceptedHere)
        {
            throw new DomainException("account_not_accepted", "This account cannot be used at this point of sale.");
        }

        if (dto.OperatorId is { } op)
        {
            await EnsureOperatorAsync(registerId, op, ct);
        }

        var request = new MovementRequest(type, dto.Amount, dto.IdempotencyKey, dto.OccurredAt)
        {
            RegisterId = registerId,
            OperatorId = dto.OperatorId,
            TicketId = dto.TicketId,
            PaymentMethod = dto.PaymentMethod is null ? null : Mappings.Parse<PaymentMethod>(dto.PaymentMethod, "payment method"),
            Comment = dto.Comment.Clean(),
            IsOfflineReplay = dto.IsOfflineReplay,
        };
        var result = await ledger.PostAsync(dto.AccountId, request, cancellationToken: ct);
        if (!result.WasDuplicate)
        {
            Operations.PosMetrics.LedgerMovements.Add(1, new KeyValuePair<string, object?>("type", dto.Type),
                new KeyValuePair<string, object?>("offline", dto.IsOfflineReplay));
        }

        return new LedgerResultDto(result.MovementId, result.AccountId, result.Amount, result.BalanceAfter, result.WasDuplicate);
    }

    public async Task<SyncAck> IngestTicketAsync(Guid registerId, TicketSyncDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var existing = await db.Tickets.AsNoTracking().Where(t => t.Id == dto.Id).Select(t => new { t.RegisterId, t.Hash }).SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing.RegisterId == registerId && existing.Hash == dto.Hash
                ? new SyncAck(dto.Id, WasDuplicate: true)
                : throw new ConflictException("A different ticket with this identifier already exists.");
        }

        var register = await db.Registers.SingleOrDefaultAsync(r => r.Id == registerId && r.IsActive, ct)
                       ?? throw new ForbiddenException("Unknown or inactive register.");
        if (dto.Sequence <= register.LastSyncedTicketSequence)
        {
            LogSequenceConflict(logger, register.TicketPrefix, dto.Sequence);
            throw new ConflictException($"Sequence {dto.Sequence} was already used by another ticket of this register.");
        }

        if (dto.Sequence != register.LastSyncedTicketSequence + 1)
        {
            throw new DomainException("sequence_gap",
                $"Ticket {dto.Sequence} received before ticket {register.LastSyncedTicketSequence + 1}: send tickets in order.");
        }

        var expectedPrevious = register.LastSyncedTicketHash ?? TicketHasher.GenesisHash;
        if (dto.PreviousHash != expectedPrevious)
        {
            LogChainBroken(logger, register.TicketPrefix, dto.Sequence);
            throw new DomainException("chain_broken", "The ticket does not chain on the last ticket received from this register.");
        }

        var session = await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == dto.CashSessionId, ct);
        if (session is null || session.RegisterId != registerId)
        {
            throw new DomainException("unknown_session", "The cash session of the ticket has not been received.");
        }

        await EnsureOperatorAsync(registerId, dto.OperatorId, ct);

        Ticket ticket;
        if (dto.Kind == nameof(TicketKind.CreditNote))
        {
            var original = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments)
                               .SingleOrDefaultAsync(t => t.Id == dto.CreditedTicketId, ct)
                           ?? throw new DomainException("unknown_original", "The credited ticket has not been received.");
            if (original.RegisterId != registerId)
            {
                throw new DomainException("invalid_credit_note", "A credit note must be issued by the register of the original ticket.");
            }

            ticket = dto.RebuildCreditNote(original, register.TicketPrefix);
        }
        else
        {
            ticket = dto.RebuildSale(registerId, register.TicketPrefix);
        }

        if (ticket.Hash != dto.Hash || ticket.Number != dto.Number)
        {
            LogHashMismatch(logger, register.TicketPrefix, dto.Sequence);
            throw new DomainException("hash_mismatch", "The ticket content does not match its hash.");
        }

        await EnsureAccountMovementsAsync(ticket, ct);

        db.Tickets.Add(ticket);
        register.LastSyncedTicketSequence = ticket.Sequence;
        register.LastSyncedTicketHash = ticket.Hash;
        register.LastSeenAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        Operations.PosMetrics.TicketsIngested.Add(1);
        return new SyncAck(dto.Id, WasDuplicate: false);
    }

    public async Task<SyncAck> IngestZReportAsync(Guid registerId, ZReportSyncDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (await db.ZReports.AsNoTracking().AnyAsync(z => z.Id == dto.Id, ct))
        {
            return new SyncAck(dto.Id, WasDuplicate: true);
        }

        var register = await db.Registers.SingleOrDefaultAsync(r => r.Id == registerId && r.IsActive, ct)
                       ?? throw new ForbiddenException("Unknown or inactive register.");
        var session = await db.CashSessions.SingleOrDefaultAsync(s => s.Id == dto.CashSessionId && s.RegisterId == registerId, ct)
                      ?? throw new DomainException("unknown_session", "The cash session has not been received.");
        if (dto.LastTicketSequence is { } last && register.LastSyncedTicketSequence < last)
        {
            throw new DomainException("tickets_missing", "Some tickets of the session have not been received yet.");
        }

        if (dto.ZNumber != register.LastZNumber + 1)
        {
            throw new ConflictException($"Z number {dto.ZNumber} is not the next one ({register.LastZNumber + 1}).");
        }

        await EnsureOperatorAsync(registerId, dto.ClosedByOperatorId, ct);
        var tickets = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments)
            .Where(t => t.CashSessionId == session.Id).ToListAsync(ct);
        var movements = await db.AccountMovements.AsNoTracking()
            .Where(m => m.RegisterId == registerId && m.PaymentMethod != null && m.OccurredAt >= session.OpenedAt && m.OccurredAt <= dto.GeneratedAt)
            .ToListAsync(ct);

        var z = ZReportCalculator.Compute(new ZReportRequest(dto.Id, session, dto.ZNumber, dto.GeneratedAt, dto.CountedCash, tickets,
            RegisterCollection.FromMovements(movements, registerId)));
        if (z.NetSales != dto.NetSales || z.TotalVat != dto.TotalVat || z.ExpectedCash != dto.ExpectedCash || z.LastTicketSequence != dto.LastTicketSequence)
        {
            LogZMismatch(logger, register.TicketPrefix, dto.ZNumber);
            throw new DomainException("z_mismatch", "The Z report totals differ from the tickets received by the server.");
        }

        db.ZReports.Add(z);
        session.Close(z, dto.ClosedByOperatorId, dto.Forced);
        register.LastZNumber = dto.ZNumber;
        register.LastSeenAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new SyncAck(dto.Id, WasDuplicate: false);
    }

    /// <summary>Every account payment / refund of the ticket must already be recorded on the ledger with a matching amount.</summary>
    private async Task EnsureAccountMovementsAsync(Ticket ticket, CancellationToken ct)
    {
        foreach (var payment in ticket.Payments.Where(p => p.Method == PaymentMethod.Account))
        {
            if (payment.AccountMovementId is not { } key)
            {
                throw new DomainException("movement_missing", "An account payment must reference its ledger movement.");
            }

            var movement = await db.AccountMovements.AsNoTracking().SingleOrDefaultAsync(m => m.IdempotencyKey == key, ct)
                           ?? throw new DomainException("movement_missing", "The account movement of the payment has not been received.");
            if (movement.AccountId != ticket.AccountId || movement.Amount != -payment.Amount)
            {
                throw new DomainException("movement_mismatch", "The account movement does not match the payment.");
            }
        }
    }

    private async Task EnsureOperatorAsync(Guid registerId, Guid operatorId, CancellationToken ct)
    {
        var scope = await new RegisterReferenceService(db, NoRowVersions.Instance, clock).LoadScopeAsync(registerId, ct);
        if (!await db.Operators.AnyAsync(o => o.Id == operatorId && o.CompanyId == scope.CompanyId, ct))
        {
            throw new DomainException("unknown_operator", "The operator does not belong to the register's company.");
        }
    }

    private async Task TouchRegisterAsync(Guid registerId, CancellationToken ct)
    {
        var register = await db.Registers.SingleAsync(r => r.Id == registerId, ct);
        register.LastSeenAt = clock.GetUtcNow();
    }

    private sealed class NoRowVersions : IRowVersionSource
    {
        public static readonly NoRowVersions Instance = new();

        public Task<ulong> GetMinActiveRowVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(0UL);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Register {Prefix}: sequence {Sequence} reused by a different ticket")]
    private static partial void LogSequenceConflict(ILogger logger, string prefix, long sequence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Register {Prefix}: ticket {Sequence} breaks the integrity chain")]
    private static partial void LogChainBroken(ILogger logger, string prefix, long sequence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Register {Prefix}: ticket {Sequence} content does not match its hash")]
    private static partial void LogHashMismatch(ILogger logger, string prefix, long sequence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Register {Prefix}: Z {ZNumber} totals differ from server tickets")]
    private static partial void LogZMismatch(ILogger logger, string prefix, int zNumber);
}
