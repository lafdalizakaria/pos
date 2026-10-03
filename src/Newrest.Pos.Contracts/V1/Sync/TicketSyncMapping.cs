using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Contracts.V1.Sync;

/// <summary>
/// Conversion between fiscal tickets and their sync form. Registers and server use the same domain code, so a ticket
/// rebuilt on the server from <see cref="TicketSyncDto"/> must produce exactly the hash computed by the register.
/// </summary>
public static class TicketSyncMapping
{
    public static TicketSyncDto ToSyncDto(this Ticket t) => new(
        t.Id, t.Kind.ToString(), t.Sequence, t.Number, t.PreviousHash, t.Hash, t.CashSessionId, t.OperatorId, t.BusinessDate, t.IssuedAt,
        t.DinerId, t.AccountId, t.BadgeNumber, t.SubsidyRuleId, t.SubsidyAmount, t.CreditedTicketId, t.CreditReason,
        [.. t.Lines.Select(l => new TicketLineSyncDto(l.ArticleId, l.ArticleCode, l.Label, l.Quantity, l.UnitPrice, l.VatRate, l.IsSubsidizable,
            l.Source.ToString()))],
        [.. t.Payments.Select(p => new PaymentSyncDto(p.Method.ToString(), p.Amount, p.Tendered, p.AuthorizationCode, p.AccountMovementId))]);

    /// <summary>Rebuilds a sale. <paramref name="registerId"/> and <paramref name="prefix"/> come from the authenticated register.</summary>
    public static Ticket RebuildSale(this TicketSyncDto dto, Guid registerId, string prefix)
    {
        ArgumentNullException.ThrowIfNull(dto);
        EnsureKind(dto, TicketKind.Sale);
        return Ticket.Issue(new TicketIssueRequest
        {
            Id = dto.Id,
            RegisterId = registerId,
            RegisterPrefix = prefix,
            Sequence = dto.Sequence,
            PreviousHash = dto.PreviousHash,
            CashSessionId = dto.CashSessionId,
            OperatorId = dto.OperatorId,
            BusinessDate = dto.BusinessDate,
            IssuedAt = dto.IssuedAt,
            Lines = [.. dto.Lines.Select(ToInput)],
            Payments = [.. dto.Payments.Select(ToInput)],
            SubsidyAmount = dto.SubsidyAmount,
            SubsidyRuleId = dto.SubsidyRuleId,
            DinerId = dto.DinerId,
            AccountId = dto.AccountId,
            BadgeNumber = dto.BadgeNumber,
        });
    }

    public static Ticket RebuildCreditNote(this TicketSyncDto dto, Ticket original, string prefix)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(original);
        EnsureKind(dto, TicketKind.CreditNote);
        if (dto.CreditedTicketId != original.Id)
        {
            throw new DomainException("invalid_credit_note", "The credit note references another ticket.");
        }

        return Ticket.IssueCreditNote(original, new CreditNoteRequest
        {
            Id = dto.Id,
            RegisterPrefix = prefix,
            Sequence = dto.Sequence,
            PreviousHash = dto.PreviousHash,
            CashSessionId = dto.CashSessionId,
            OperatorId = dto.OperatorId,
            BusinessDate = dto.BusinessDate,
            IssuedAt = dto.IssuedAt,
            Reason = dto.CreditReason ?? string.Empty,
            RefundPayments = [.. dto.Payments.Select(ToInput)],
        });
    }

    private static void EnsureKind(TicketSyncDto dto, TicketKind expected)
    {
        if (!Enum.TryParse<TicketKind>(dto.Kind, out var kind) || kind != expected)
        {
            throw new DomainException("invalid_ticket_kind", $"Expected a {expected} ticket.");
        }
    }

    private static TicketLineInput ToInput(TicketLineSyncDto l) => new(l.ArticleId, l.ArticleCode, l.Label, l.Quantity, l.UnitPrice, l.VatRate,
        l.IsSubsidizable, Enum.TryParse<LineSource>(l.Source, out var source) ? source : LineSource.Manual);

    private static PaymentInput ToInput(PaymentSyncDto p) =>
        new(Enum.TryParse<PaymentMethod>(p.Method, out var method) ? method : throw new DomainException("invalid_payment", $"Unknown payment method {p.Method}."),
            p.Amount)
        {
            Tendered = p.Tendered,
            AuthorizationCode = p.AuthorizationCode,
            AccountMovementId = p.AccountMovementId,
        };
}
