using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Sales;

/// <summary>
/// Integrity chain: <c>Hash = SHA-256(canonical JSON of the ticket content + PreviousHash)</c>, lowercase hex.
/// The first ticket of a register chains on <see cref="GenesisHash"/>. Any change to a stored ticket, or the
/// deletion/insertion of a ticket, breaks the chain from that point on.
/// </summary>
public static class TicketHasher
{
    public const string Version = "v1";
    public static readonly string GenesisHash = new('0', 64);

    public static string ComputeHash(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var bytes = Encoding.UTF8.GetBytes(Canonicalize(ticket));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigitLower))
        {
            throw new DomainException("invalid_hash", "A chain hash is 64 lowercase hexadecimal characters.");
        }

        return hash;
    }

    /// <summary>
    /// Canonical form: fixed property order, invariant formatting, decimals with a fixed scale, UTC timestamps.
    /// Changing this format requires a new <see cref="Version"/> and keeping the old one for verification.
    /// </summary>
    public static string Canonicalize(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var payload = new CanonicalTicket(
            Version,
            ticket.Id.ToString("D"),
            ticket.Kind.ToString(),
            ticket.RegisterId.ToString("D"),
            ticket.Number,
            ticket.Sequence,
            ticket.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ticket.IssuedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ticket.CashSessionId.ToString("D"),
            ticket.OperatorId.ToString("D"),
            ticket.DinerId?.ToString("D"),
            ticket.AccountId?.ToString("D"),
            ticket.BadgeNumber,
            ticket.CreditedTicketId?.ToString("D"),
            ticket.CreditReason,
            Amount(ticket.TotalAmount),
            Amount(ticket.TotalVat),
            Amount(ticket.SubsidyAmount),
            Amount(ticket.DinerShare),
            [.. ticket.Lines.OrderBy(l => l.LineNumber).Select(l => new CanonicalLine(
                l.LineNumber, l.ArticleId.ToString("D"), l.ArticleCode, l.Label, l.Quantity,
                Amount(l.UnitPrice), Rate(l.VatRate), Amount(l.LineTotal), Amount(l.VatAmount)))],
            [.. ticket.Payments.OrderBy(p => p.Index).Select(p => new CanonicalPayment(
                p.Index, p.Method.ToString(), Amount(p.Amount), p.Tendered is { } t ? Amount(t) : null, p.AuthorizationCode))],
            ticket.PreviousHash);
        return JsonSerializer.Serialize(payload, CanonicalJsonContext.Default.CanonicalTicket);
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);
}

internal sealed record CanonicalTicket(
    string V, string Id, string Kind, string Register, string Number, long Seq, string Date, string IssuedAt,
    string Session, string Operator, string? Diner, string? Account, string? Badge, string? Credited, string? Reason,
    string Total, string Vat, string Subsidy, string DinerShare,
    IReadOnlyList<CanonicalLine> Lines, IReadOnlyList<CanonicalPayment> Payments, string Prev);

internal sealed record CanonicalLine(
    int N, string Article, string Code, string Label, int Qty, string Unit, string Rate, string Total, string Vat);

internal sealed record CanonicalPayment(int N, string Method, string Amount, string? Tendered, string? Auth);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = false)]
[System.Text.Json.Serialization.JsonSerializable(typeof(CanonicalTicket))]
internal sealed partial class CanonicalJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

public enum ChainIssueKind
{
    /// <summary>A sequence number is missing.</summary>
    Gap,

    /// <summary>A sequence number appears twice.</summary>
    Duplicate,

    /// <summary>PreviousHash does not match the hash of the previous ticket.</summary>
    BrokenLink,

    /// <summary>The stored hash does not match the content: the ticket was altered.</summary>
    HashMismatch,

    /// <summary>A ticket belongs to another register.</summary>
    ForeignRegister,
}

public sealed record ChainIssue(long Sequence, ChainIssueKind Kind, string Detail);

public sealed record ChainVerificationResult(int TicketsChecked, IReadOnlyList<ChainIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public static class TicketChainVerifier
{
    /// <summary>
    /// Verifies the chain of one register. <paramref name="firstExpectedSequence"/> and
    /// <paramref name="expectedPreviousHash"/> allow verifying from a checkpoint (e.g. after archiving).
    /// </summary>
    public static ChainVerificationResult Verify(Guid registerId, IEnumerable<Ticket> tickets, long firstExpectedSequence = 1,
        string? expectedPreviousHash = null)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        var issues = new List<ChainIssue>();
        var ordered = tickets.OrderBy(t => t.Sequence).ToList();
        var expectedSequence = firstExpectedSequence;
        var previousHash = expectedPreviousHash ?? (firstExpectedSequence == 1 ? TicketHasher.GenesisHash : null);

        foreach (var ticket in ordered)
        {
            if (ticket.RegisterId != registerId)
            {
                issues.Add(new ChainIssue(ticket.Sequence, ChainIssueKind.ForeignRegister, $"Ticket {ticket.Number} belongs to another register."));
                continue;
            }

            if (ticket.Sequence < expectedSequence)
            {
                issues.Add(new ChainIssue(ticket.Sequence, ChainIssueKind.Duplicate, $"Sequence {ticket.Sequence} appears more than once."));
                continue;
            }

            for (var missing = expectedSequence; missing < ticket.Sequence; missing++)
            {
                issues.Add(new ChainIssue(missing, ChainIssueKind.Gap, $"Sequence {missing} is missing."));
            }

            if (previousHash is not null && ticket.PreviousHash != previousHash)
            {
                issues.Add(new ChainIssue(ticket.Sequence, ChainIssueKind.BrokenLink, $"Ticket {ticket.Number} does not chain on the previous ticket."));
            }

            if (!ticket.HasValidHash())
            {
                issues.Add(new ChainIssue(ticket.Sequence, ChainIssueKind.HashMismatch, $"Ticket {ticket.Number} content does not match its hash."));
            }

            previousHash = ticket.Hash;
            expectedSequence = ticket.Sequence + 1;
        }

        return new ChainVerificationResult(ordered.Count, issues);
    }
}
