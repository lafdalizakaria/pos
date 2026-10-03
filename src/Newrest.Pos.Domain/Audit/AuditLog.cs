using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Audit;

/// <summary>Append-only trail of sensitive actions (who, what, when, before/after).</summary>
public sealed class AuditLog : Entity, IImmutableRecord
{
    private AuditLog()
    {
    }

    public AuditLog(DateTimeOffset occurredAt, string actor, string action, string entityType, string? entityId,
        string? beforeJson = null, string? afterJson = null)
    {
        OccurredAt = occurredAt;
        Actor = Guard.NotBlank(actor, nameof(actor), 200);
        Action = Guard.NotBlank(action, nameof(action), 100);
        EntityType = Guard.NotBlank(entityType, nameof(entityType), 100);
        EntityId = entityId;
        BeforeJson = beforeJson;
        AfterJson = afterJson;
    }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>UPN of a back-office user or "operator:{code}" / "register:{code}".</summary>
    public string Actor { get; private set; } = null!;

    public string Action { get; private set; } = null!;
    public string EntityType { get; private set; } = null!;
    public string? EntityId { get; private set; }
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }
    public Guid? CompanyId { get; init; }
    public Guid? SiteId { get; init; }
    public string? IpAddress { get; init; }
    public string? CorrelationId { get; init; }
}
