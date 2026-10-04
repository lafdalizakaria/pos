using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Sales;

public enum ZSection
{
    /// <summary>Ticket payments by method (net of credit-note refunds).</summary>
    Payment,

    /// <summary>Sales by VAT rate: base amount = TTC, tax = VAT.</summary>
    Vat,

    /// <summary>Account top-ups / payouts collected at the register, by payment method.</summary>
    AccountTopUp,
}

/// <summary>Daily closing of a register session. Immutable once generated.</summary>
public sealed class ZReport : Entity, IImmutableRecord
{
    private readonly List<ZReportLine> _lines = [];

    private ZReport()
    {
    }

    internal ZReport(Guid id) : base(id)
    {
    }

    public Guid RegisterId { get; internal set; }
    public Guid CashSessionId { get; internal set; }
    public int ZNumber { get; internal set; }
    public DateOnly BusinessDate { get; internal set; }
    public DateTimeOffset OpenedAt { get; internal set; }
    public DateTimeOffset GeneratedAt { get; internal set; }
    public long? FirstTicketSequence { get; internal set; }
    public long? LastTicketSequence { get; internal set; }
    public int SaleCount { get; internal set; }
    public int CreditNoteCount { get; internal set; }
    public decimal GrossSales { get; internal set; }
    public decimal CreditNotesTotal { get; internal set; }
    public decimal NetSales { get; internal set; }
    public decimal TotalVat { get; internal set; }
    public decimal SubsidyTotal { get; internal set; }
    public decimal DinerShareTotal { get; internal set; }
    public decimal AccountTopUpTotal { get; internal set; }
    public decimal OpeningFloat { get; internal set; }
    public decimal ExpectedCash { get; internal set; }
    public decimal CountedCash { get; internal set; }
    public decimal CashDifference { get; internal set; }

    /// <summary>Hash of the last ticket included, ties the Z to the ticket chain.</summary>
    public string? LastTicketHash { get; internal set; }

    public IReadOnlyList<ZReportLine> Lines => _lines;

    internal void AddLine(ZSection section, string key, int count, decimal amount, decimal? baseAmount = null, decimal? taxAmount = null)
        => _lines.Add(new ZReportLine(Id, section, key, count, amount, baseAmount, taxAmount));
}

public sealed class ZReportLine : Entity, IImmutableRecord
{
    private ZReportLine()
    {
    }

    internal ZReportLine(Guid zReportId, ZSection section, string key, int count, decimal amount, decimal? baseAmount, decimal? taxAmount)
    {
        ZReportId = zReportId;
        Section = section;
        Key = key;
        Count = count;
        Amount = amount;
        BaseAmount = baseAmount;
        TaxAmount = taxAmount;
    }

    public Guid ZReportId { get; private set; }
    public ZSection Section { get; private set; }
    public string Key { get; private set; } = null!;
    public int Count { get; private set; }
    public decimal Amount { get; private set; }
    public decimal? BaseAmount { get; private set; }
    public decimal? TaxAmount { get; private set; }
}

/// <summary>Money collected at the register outside tickets: account top-ups (+) and balance payouts (−), by method.</summary>
public sealed record RegisterCollection(PaymentMethod Method, decimal Amount)
{
    /// <summary>Collections of a register from its ledger movements (those carrying a payment method other than Account).</summary>
    public static IReadOnlyList<RegisterCollection> FromMovements(IEnumerable<AccountMovement> movements, Guid registerId) =>
        [.. movements.Where(m => m.RegisterId == registerId && m.PaymentMethod is { } method && method != PaymentMethod.Account)
            .Select(m => new RegisterCollection(m.PaymentMethod!.Value, m.Amount))];
}

public sealed record ZReportRequest(
    Guid Id,
    CashSession Session,
    int ZNumber,
    DateTimeOffset GeneratedAt,
    decimal CountedCash,
    IReadOnlyCollection<Ticket> Tickets,
    IReadOnlyCollection<RegisterCollection> Collections);

public static class ZReportCalculator
{
    /// <summary>
    /// Builds the Z report of a session from its tickets and the money collected at the register for accounts
    /// (top-ups and payouts, see <see cref="RegisterCollection"/>). Top-ups are not sales:
    /// they appear in their own section and in expected cash, never in sales or VAT.
    /// </summary>
    public static ZReport Compute(ZReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = request.Session;
        var tickets = request.Tickets.Where(t => t.CashSessionId == session.Id).OrderBy(t => t.Sequence).ToList();
        if (tickets.Count != request.Tickets.Count)
        {
            throw new DomainException("z_foreign_ticket", "Every ticket must belong to the closed session.");
        }

        var collected = request.Collections.Where(c => c.Method != PaymentMethod.Account).ToList();

        var report = new ZReport(request.Id)
        {
            RegisterId = session.RegisterId,
            CashSessionId = session.Id,
            ZNumber = request.ZNumber > 0 ? request.ZNumber : throw new DomainException("invalid_z_number", "Z numbers start at 1."),
            BusinessDate = session.BusinessDate,
            OpenedAt = session.OpenedAt,
            GeneratedAt = request.GeneratedAt,
            FirstTicketSequence = tickets.FirstOrDefault()?.Sequence,
            LastTicketSequence = tickets.LastOrDefault()?.Sequence,
            LastTicketHash = tickets.LastOrDefault()?.Hash,
            SaleCount = tickets.Count(t => t.Kind == TicketKind.Sale),
            CreditNoteCount = tickets.Count(t => t.Kind == TicketKind.CreditNote),
            GrossSales = tickets.Where(t => t.Kind == TicketKind.Sale).Sum(t => t.TotalAmount),
            CreditNotesTotal = tickets.Where(t => t.Kind == TicketKind.CreditNote).Sum(t => t.TotalAmount),
            TotalVat = tickets.Sum(t => t.TotalVat),
            SubsidyTotal = tickets.Sum(t => t.SubsidyAmount),
            DinerShareTotal = tickets.Sum(t => t.DinerShare),
            AccountTopUpTotal = collected.Sum(m => m.Amount),
            OpeningFloat = session.OpeningFloat,
            CountedCash = Money.EnsureValid(Guard.NotNegative(request.CountedCash, nameof(request.CountedCash)), nameof(request.CountedCash)),
        };
        report.NetSales = report.GrossSales + report.CreditNotesTotal;

        foreach (var group in tickets.SelectMany(t => t.Payments).GroupBy(p => p.Method).OrderBy(g => g.Key))
        {
            report.AddLine(ZSection.Payment, group.Key.ToString(), group.Count(), group.Sum(p => p.Amount));
        }

        foreach (var group in tickets.SelectMany(t => t.Lines).GroupBy(l => l.VatRate).OrderBy(g => g.Key))
        {
            var ttc = group.Sum(l => l.LineTotal);
            var vat = group.Sum(l => l.VatAmount);
            report.AddLine(ZSection.Vat, group.Key.ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture),
                group.Count(), ttc, ttc - vat, vat);
        }

        foreach (var group in collected.GroupBy(m => m.Method).OrderBy(g => g.Key))
        {
            report.AddLine(ZSection.AccountTopUp, group.Key.ToString(), group.Count(), group.Sum(m => m.Amount));
        }

        var cashFromTickets = tickets.SelectMany(t => t.Payments).Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
        var cashFromAccounts = collected.Where(m => m.Method == PaymentMethod.Cash).Sum(m => m.Amount);
        report.ExpectedCash = session.OpeningFloat + cashFromTickets + cashFromAccounts;
        report.CashDifference = report.CountedCash - report.ExpectedCash;
        return report;
    }
}
