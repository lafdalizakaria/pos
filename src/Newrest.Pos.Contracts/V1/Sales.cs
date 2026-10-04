namespace Newrest.Pos.Contracts.V1;

public sealed record TicketSummaryDto(Guid Id, string Number, string Kind, Guid RegisterId, string RegisterPrefix, DateOnly BusinessDate,
    DateTimeOffset IssuedAt, decimal TotalAmount, decimal SubsidyAmount, decimal DinerShare, string? DinerName, string? BadgeNumber,
    Guid? CreditedTicketId, bool IsCredited);

public sealed record TicketLineDto(int LineNumber, string ArticleCode, string Label, int Quantity, decimal UnitPrice, decimal VatRate,
    decimal LineTotal, decimal VatAmount, string Source);

public sealed record PaymentDto(string Method, decimal Amount, decimal? Tendered, decimal? Change, string? AuthorizationCode);

public sealed record TicketDetailDto(TicketSummaryDto Summary, long Sequence, string OperatorCode, string? CreditReason, decimal TotalVat,
    string PreviousHash, string Hash, DateTimeOffset ReceivedAt, IReadOnlyList<TicketLineDto> Lines, IReadOnlyList<PaymentDto> Payments);

public sealed record ChainIssueDto(long Sequence, string Kind, string Detail);

public sealed record ChainVerificationDto(Guid RegisterId, string RegisterPrefix, int TicketsChecked, long LastSequence, bool IsValid,
    IReadOnlyList<ChainIssueDto> Issues, DateTimeOffset VerifiedAt);

public sealed record ZReportLineDto(string Section, string Key, int Count, decimal Amount, decimal? BaseAmount, decimal? TaxAmount);

public sealed record ZReportDto(Guid Id, Guid RegisterId, string RegisterPrefix, int ZNumber, DateOnly BusinessDate, DateTimeOffset OpenedAt,
    DateTimeOffset GeneratedAt, long? FirstTicketSequence, long? LastTicketSequence, int SaleCount, int CreditNoteCount, decimal GrossSales,
    decimal CreditNotesTotal, decimal NetSales, decimal TotalVat, decimal SubsidyTotal, decimal AccountTopUpTotal, decimal ExpectedCash,
    decimal CountedCash, decimal CashDifference, IReadOnlyList<ZReportLineDto> Lines);
