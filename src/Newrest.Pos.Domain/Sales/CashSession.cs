using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Sales;

public enum CashSessionStatus
{
    Open,
    Closed,
}

/// <summary>Register shift: from opening (cash float) to closing (Z report).</summary>
public sealed class CashSession : Entity
{
    private CashSession()
    {
    }

    public CashSession(Guid id, Guid registerId, Guid operatorId, decimal openingFloat, DateTimeOffset openedAt, DateOnly businessDate)
        : base(id)
    {
        RegisterId = Guard.NotEmpty(registerId, nameof(registerId));
        OpenedByOperatorId = Guard.NotEmpty(operatorId, nameof(operatorId));
        OpeningFloat = Money.EnsureValid(Guard.NotNegative(openingFloat, nameof(openingFloat)), nameof(openingFloat));
        OpenedAt = openedAt;
        BusinessDate = businessDate;
        Status = CashSessionStatus.Open;
    }

    public Guid RegisterId { get; private set; }
    public Guid OpenedByOperatorId { get; private set; }
    public DateOnly BusinessDate { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public decimal OpeningFloat { get; private set; }
    public CashSessionStatus Status { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedByOperatorId { get; private set; }
    public bool WasForced { get; private set; }
    public Guid? ZReportId { get; private set; }

    public void Close(ZReport report, Guid operatorId, bool forced = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (Status == CashSessionStatus.Closed)
        {
            throw new DomainException("session_closed", "The cash session is already closed.");
        }

        if (report.CashSessionId != Id)
        {
            throw new DomainException("z_other_session", "The Z report belongs to another session.");
        }

        Status = CashSessionStatus.Closed;
        ClosedAt = report.GeneratedAt;
        ClosedByOperatorId = Guard.NotEmpty(operatorId, nameof(operatorId));
        WasForced = forced;
        ZReportId = report.Id;
    }

    public void EnsureOpen()
    {
        if (Status != CashSessionStatus.Open)
        {
            throw new DomainException("session_closed", "The cash session is closed.");
        }
    }
}
