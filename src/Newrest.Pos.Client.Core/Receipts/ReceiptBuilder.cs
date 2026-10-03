using System.Globalization;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Devices.Printing;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Client.Core.Receipts;

/// <summary>
/// Receipt layouts (French). Mentions printed: legal name, ICE / IF / RC, site, ticket number, date and time, cashier,
/// lines, VAT by rate, total, employer subsidy, diner share, payments, start of the integrity hash.
/// The list of mandatory mentions is to be validated (docs/compliance.md).
/// </summary>
public sealed class ReceiptBuilder(IReceiptPrinter printer)
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-MA");

    private int Width => printer.LineWidth;

    public ReceiptDocument BuildTicket(Ticket ticket, RegisterProfileDto profile, string cashierName, string? dinerName = null, decimal? balanceAfter = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(profile);
        var lines = Header(profile);
        lines.Add(new ReceiptLine(ticket.Kind == TicketKind.CreditNote ? "AVOIR" : "TICKET", ReceiptAlignment.Center, Bold: true, Large: true));
        lines.Add(new ReceiptLine(ticket.Number, ReceiptAlignment.Center, Bold: true));
        if (ticket.CreditReason is { } reason)
        {
            lines.Add(new ReceiptLine($"Motif : {reason}"));
        }

        lines.Add(new ReceiptLine($"{LocalTime(ticket.IssuedAt, profile)}  Caisse {profile.Code}"));
        lines.Add(new ReceiptLine($"Caissier : {cashierName}"));
        if (dinerName is not null)
        {
            lines.Add(new ReceiptLine($"Convive : {dinerName}"));
        }

        lines.Add(Sep());
        foreach (var l in ticket.Lines)
        {
            lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns($"{l.Quantity} x {l.Label}", Amount(l.LineTotal), Width)));
            if (l.Quantity is not (1 or -1))
            {
                lines.Add(new ReceiptLine($"    à {Amount(l.UnitPrice)}"));
            }
        }

        lines.Add(Sep());
        foreach (var vat in ticket.Lines.GroupBy(l => l.VatRate).OrderBy(g => g.Key))
        {
            var ttc = vat.Sum(l => l.LineTotal);
            var tax = vat.Sum(l => l.VatAmount);
            lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns($"TVA {vat.Key * 100:0.##}% HT {Amount(ttc - tax)}", Amount(tax), Width)));
        }

        lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns("TOTAL TTC", Amount(ticket.TotalAmount) + " MAD", Width / 2), ReceiptAlignment.Left, Bold: true, Large: true));
        if (ticket.SubsidyAmount != 0)
        {
            lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns("Subvention employeur", Amount(ticket.SubsidyAmount), Width)));
            lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns("Part convive", Amount(ticket.DinerShare), Width), Bold: true));
        }

        foreach (var p in ticket.Payments)
        {
            lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns(PaymentLabel(p), Amount(p.Amount), Width)));
            if (p.Change is { } change && change != 0)
            {
                lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns($"  Reçu {Amount(p.Tendered!.Value)} — Rendu", Amount(change), Width)));
            }
        }

        if (balanceAfter is { } balance)
        {
            lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns("Solde du compte", Amount(balance), Width)));
        }

        lines.Add(Sep());
        lines.Add(new ReceiptLine($"Empreinte {ticket.Hash[..16]}", ReceiptAlignment.Center));
        lines.Add(new ReceiptLine("Merci et bon appétit !", ReceiptAlignment.Center));
        return new ReceiptDocument(lines, Cut: true, OpenDrawer: ticket.Payments.Any(p => p.Method == PaymentMethod.Cash));
    }

    public ReceiptDocument BuildTopUp(RegisterProfileDto profile, string cashierName, string dinerName, decimal amount, string method,
        decimal? balanceAfter, DateTimeOffset at)
    {
        var lines = Header(profile);
        lines.Add(new ReceiptLine("REÇU DE RECHARGE", ReceiptAlignment.Center, Bold: true));
        lines.Add(new ReceiptLine("(n'est pas une facture)", ReceiptAlignment.Center));
        lines.Add(new ReceiptLine($"{LocalTime(at, profile)}  Caisse {profile.Code}"));
        lines.Add(new ReceiptLine($"Caissier : {cashierName}"));
        lines.Add(new ReceiptLine($"Convive : {dinerName}"));
        lines.Add(Sep());
        lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns($"Recharge ({MethodLabel(method)})", Amount(amount), Width), Bold: true));
        if (balanceAfter is { } balance)
        {
            lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns("Nouveau solde", Amount(balance), Width)));
        }

        return new ReceiptDocument(lines, Cut: true, OpenDrawer: method == nameof(PaymentMethod.Cash));
    }

    public ReceiptDocument BuildZ(ZReport z, RegisterProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(z);
        var lines = Header(profile);
        lines.Add(new ReceiptLine($"CLÔTURE Z N° {z.ZNumber}", ReceiptAlignment.Center, Bold: true, Large: true));
        lines.Add(new ReceiptLine($"Journée du {z.BusinessDate.ToString("dd/MM/yyyy", French)} — Caisse {profile.Code}"));
        lines.Add(new ReceiptLine($"Du {LocalTime(z.OpenedAt, profile)} au {LocalTime(z.GeneratedAt, profile)}"));
        lines.Add(new ReceiptLine($"Tickets {z.FirstTicketSequence?.ToString(CultureInfo.InvariantCulture) ?? "-"} à {z.LastTicketSequence?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
        lines.Add(Sep());
        lines.Add(Row($"Ventes ({z.SaleCount})", z.GrossSales));
        lines.Add(Row($"Avoirs ({z.CreditNoteCount})", z.CreditNotesTotal));
        lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns("CA NET TTC", Amount(z.NetSales), Width), Bold: true));
        lines.Add(Row("Subventions employeur", z.SubsidyTotal));
        lines.Add(Row("Part convives", z.DinerShareTotal));
        lines.Add(Sep());
        lines.Add(new ReceiptLine("TVA", Bold: true));
        foreach (var l in z.Lines.Where(l => l.Section == ZSection.Vat))
        {
            lines.Add(Row($"{decimal.Parse(l.Key, CultureInfo.InvariantCulture) * 100:0.##}% HT {Amount(l.BaseAmount ?? 0)}", l.TaxAmount ?? 0));
        }

        lines.Add(new ReceiptLine("Encaissements tickets", Bold: true));
        foreach (var l in z.Lines.Where(l => l.Section == ZSection.Payment))
        {
            lines.Add(Row($"{MethodLabel(l.Key)} ({l.Count})", l.Amount));
        }

        if (z.Lines.Any(l => l.Section == ZSection.AccountTopUp))
        {
            lines.Add(new ReceiptLine("Recharges de comptes (hors CA)", Bold: true));
            foreach (var l in z.Lines.Where(l => l.Section == ZSection.AccountTopUp))
            {
                lines.Add(Row($"{MethodLabel(l.Key)} ({l.Count})", l.Amount));
            }
        }

        lines.Add(Sep());
        lines.Add(Row("Fond de caisse", z.OpeningFloat));
        lines.Add(Row("Espèces attendues", z.ExpectedCash));
        lines.Add(Row("Espèces comptées", z.CountedCash));
        lines.Add(new ReceiptLine(ReceiptLayout.TwoColumns("ÉCART", Amount(z.CashDifference), Width), Bold: true));
        if (z.LastTicketHash is { } hash)
        {
            lines.Add(new ReceiptLine($"Dernière empreinte {hash[..16]}", ReceiptAlignment.Center));
        }

        return new ReceiptDocument(lines, Cut: true);
    }

    private List<ReceiptLine> Header(RegisterProfileDto p)
    {
        var lines = new List<ReceiptLine> { new(p.LegalName, ReceiptAlignment.Center, Bold: true) };
        lines.AddRange(ReceiptLayout.Wrap($"{p.SiteName} {p.SiteAddress}".Trim(), Width).Select(l => new ReceiptLine(l, ReceiptAlignment.Center)));
        var ids = string.Join(" ", new[] { p.Ice is null ? null : $"ICE {p.Ice}", p.TaxId is null ? null : $"IF {p.TaxId}",
            p.TradeRegister is null ? null : $"RC {p.TradeRegister}" }.Where(x => x is not null));
        lines.AddRange(ReceiptLayout.Wrap(ids, Width).Select(l => new ReceiptLine(l, ReceiptAlignment.Center)));
        lines.Add(Sep());
        return lines;
    }

    private ReceiptLine Row(string label, decimal amount) => new(ReceiptLayout.TwoColumns(label, Amount(amount), Width));

    private ReceiptLine Sep() => new(ReceiptLayout.Separator(Width));

    public static string Amount(decimal value) => value.ToString("#,##0.00", French);

    private static string LocalTime(DateTimeOffset at, RegisterProfileDto p)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(p.SiteTimeZone, out var tz) ? tz : TimeZoneInfo.Utc;
        return TimeZoneInfo.ConvertTime(at, zone).ToString("dd/MM/yyyy HH:mm", French);
    }

    private static string PaymentLabel(Payment p) => p.Method switch
    {
        PaymentMethod.Card => $"Carte (aut. {p.AuthorizationCode})",
        _ => MethodLabel(p.Method.ToString()),
    };

    public static string MethodLabel(string method) => method switch
    {
        nameof(PaymentMethod.Cash) => "Espèces",
        nameof(PaymentMethod.Card) => "Carte bancaire",
        nameof(PaymentMethod.Account) => "Compte convive",
        nameof(PaymentMethod.BankTransfer) => "Virement",
        _ => method,
    };
}
