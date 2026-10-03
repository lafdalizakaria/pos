using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Application.Sales;

/// <summary>Back-office consultation of tickets, credit notes, Z reports and integrity chain verification (scoped by site).</summary>
public sealed class TicketQueryService(IPosDbContext db, AccessControl access, TimeProvider clock)
{
    public async Task<PagedResult<TicketSummaryDto>> ListAsync(Guid? pointOfSaleId, Guid? registerId, DateOnly? from, DateOnly? to, string? search,
        int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));
        var registers = await RegistersInScopeAsync(scope, pointOfSaleId, ct);
        var ids = registers.Keys.ToList();
        var query = db.Tickets.AsNoTracking().Where(t => ids.Contains(t.RegisterId));
        if (registerId is { } r)
        {
            query = query.Where(t => t.RegisterId == r);
        }

        if (from is { } f)
        {
            query = query.Where(t => t.BusinessDate >= f);
        }

        if (to is { } u)
        {
            query = query.Where(t => t.BusinessDate <= u);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(t => t.Number.Contains(term) || (t.BadgeNumber != null && t.BadgeNumber.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var tickets = await query.OrderByDescending(t => t.IssuedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<TicketSummaryDto>(await SummariesAsync(tickets, registers, ct), total, page, pageSize);
    }

    public async Task<TicketDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments).SingleOrDefaultAsync(t => t.Id == id, ct)
                     ?? throw new NotFoundException("Ticket", id);
        var registers = await EnsureRegisterAccessAsync(ticket.RegisterId, ct);
        var summary = (await SummariesAsync([ticket], registers, ct))[0];
        var operatorCode = await db.Operators.AsNoTracking().Where(o => o.Id == ticket.OperatorId).Select(o => o.Code).SingleOrDefaultAsync(ct) ?? "?";
        return new TicketDetailDto(summary, ticket.Sequence, operatorCode, ticket.CreditReason, ticket.TotalVat, ticket.PreviousHash, ticket.Hash,
            ticket.ReceivedAt,
            [.. ticket.Lines.OrderBy(l => l.LineNumber).Select(l => new TicketLineDto(l.LineNumber, l.ArticleCode, l.Label, l.Quantity, l.UnitPrice,
                l.VatRate, l.LineTotal, l.VatAmount, l.Source.ToString()))],
            [.. ticket.Payments.OrderBy(p => p.Index).Select(p => new PaymentDto(p.Method.ToString(), p.Amount, p.Tendered, p.Change, p.AuthorizationCode))]);
    }

    /// <summary>Recomputes every hash of the register and checks sequence continuity and chaining.</summary>
    public async Task<ChainVerificationDto> VerifyChainAsync(Guid registerId, CancellationToken ct = default)
    {
        var registers = await EnsureRegisterAccessAsync(registerId, ct);
        var tickets = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments)
            .Where(t => t.RegisterId == registerId).OrderBy(t => t.Sequence).ToListAsync(ct);
        var result = TicketChainVerifier.Verify(registerId, tickets);
        return new ChainVerificationDto(registerId, registers[registerId], result.TicketsChecked, tickets.LastOrDefault()?.Sequence ?? 0, result.IsValid,
            [.. result.Issues.Select(i => new ChainIssueDto(i.Sequence, i.Kind.ToString(), i.Detail))], clock.GetUtcNow());
    }

    public async Task<IReadOnlyList<ZReportDto>> ListZReportsAsync(Guid? pointOfSaleId, Guid? registerId, DateOnly? from, DateOnly? to,
        CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        var registers = await RegistersInScopeAsync(scope, pointOfSaleId, ct);
        var ids = registers.Keys.ToList();
        var query = db.ZReports.AsNoTracking().Include(z => z.Lines).Where(z => ids.Contains(z.RegisterId));
        if (registerId is { } r)
        {
            query = query.Where(z => z.RegisterId == r);
        }

        if (from is { } f)
        {
            query = query.Where(z => z.BusinessDate >= f);
        }

        if (to is { } u)
        {
            query = query.Where(z => z.BusinessDate <= u);
        }

        var reports = await query.OrderByDescending(z => z.GeneratedAt).Take(500).ToListAsync(ct);
        return [.. reports.Select(z => new ZReportDto(z.Id, z.RegisterId, registers[z.RegisterId], z.ZNumber, z.BusinessDate, z.OpenedAt, z.GeneratedAt,
            z.FirstTicketSequence, z.LastTicketSequence, z.SaleCount, z.CreditNoteCount, z.GrossSales, z.CreditNotesTotal, z.NetSales, z.TotalVat,
            z.SubsidyTotal, z.AccountTopUpTotal, z.ExpectedCash, z.CountedCash, z.CashDifference,
            [.. z.Lines.OrderBy(l => l.Section).ThenBy(l => l.Key).Select(l => new ZReportLineDto(l.Section.ToString(), l.Key, l.Count, l.Amount,
                l.BaseAmount, l.TaxAmount))]))];
    }

    private async Task<Dictionary<Guid, string>> RegistersInScopeAsync(UserScope scope, Guid? pointOfSaleId, CancellationToken ct)
    {
        var rows = await (from r in db.Registers.AsNoTracking()
                          join p in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals p.Id
                          join s in db.Sites.AsNoTracking() on p.SiteId equals s.Id
                          where pointOfSaleId == null || p.Id == pointOfSaleId
                          select new { r.Id, r.TicketPrefix, s.CompanyId, SiteId = s.Id }).ToListAsync(ct);
        return rows.Where(r => scope.CanAccessSite(r.CompanyId, r.SiteId)).ToDictionary(r => r.Id, r => r.TicketPrefix);
    }

    private async Task<Dictionary<Guid, string>> EnsureRegisterAccessAsync(Guid registerId, CancellationToken ct)
    {
        var posId = await db.Registers.AsNoTracking().Where(r => r.Id == registerId).Select(r => (Guid?)r.PointOfSaleId).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Register", registerId);
        await access.EnsureCanAccessPointOfSaleAsync(posId, write: false, ct);
        var prefix = await db.Registers.AsNoTracking().Where(r => r.Id == registerId).Select(r => r.TicketPrefix).SingleAsync(ct);
        return new Dictionary<Guid, string> { [registerId] = prefix };
    }

    private async Task<List<TicketSummaryDto>> SummariesAsync(IReadOnlyCollection<Ticket> tickets, IReadOnlyDictionary<Guid, string> registers,
        CancellationToken ct)
    {
        var dinerIds = tickets.Where(t => t.DinerId != null).Select(t => t.DinerId!.Value).Distinct().ToList();
        var names = await db.Diners.AsNoTracking().Where(d => dinerIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.FirstName + " " + d.LastName, ct);
        var ids = tickets.Select(t => t.Id).ToList();
        var credited = (await db.Tickets.AsNoTracking().Where(t => t.CreditedTicketId != null && ids.Contains(t.CreditedTicketId.Value))
            .Select(t => t.CreditedTicketId!.Value).ToListAsync(ct)).ToHashSet();
        return [.. tickets.Select(t => new TicketSummaryDto(t.Id, t.Number, t.Kind.ToString(), t.RegisterId, registers.GetValueOrDefault(t.RegisterId, "?"),
            t.BusinessDate, t.IssuedAt, t.TotalAmount, t.SubsidyAmount, t.DinerShare, t.DinerId is { } d ? names.GetValueOrDefault(d) : null, t.BadgeNumber,
            t.CreditedTicketId, credited.Contains(t.Id)))];
    }
}
