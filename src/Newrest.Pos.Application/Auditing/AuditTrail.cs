using System.Text.Json;
using System.Text.Json.Serialization;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Domain.Audit;

namespace Newrest.Pos.Application.Auditing;

/// <summary>
/// Builds audit entries for sensitive actions. Entries are added to the same unit of work as the change,
/// so the change and its audit are committed (or rolled back) together.
/// </summary>
public sealed class AuditTrail(IPosDbContext db, ICurrentUser user, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public AuditLog Create(string action, string entityType, object? entityId, object? before = null, object? after = null,
        Guid? companyId = null, Guid? siteId = null) =>
        new(clock.GetUtcNow(), user.IsAuthenticated ? user.Name : "anonymous", action, entityType, entityId?.ToString(),
            Serialize(before), Serialize(after))
        {
            CompanyId = companyId,
            SiteId = siteId,
            IpAddress = user.IpAddress,
            CorrelationId = user.CorrelationId,
        };

    /// <summary>Adds the entry to the pending unit of work (saved by the caller's SaveChanges).</summary>
    public AuditLog Record(string action, string entityType, object? entityId, object? before = null, object? after = null,
        Guid? companyId = null, Guid? siteId = null)
    {
        var entry = Create(action, entityType, entityId, before, after, companyId, siteId);
        db.AuditLogs.Add(entry);
        return entry;
    }

    private static string? Serialize(object? value) => value is null ? null : JsonSerializer.Serialize(value, Json);
}

/// <summary>Stable audit action names (used by filters and reports).</summary>
public static class AuditActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
    public const string PriceChanged = "PriceChanged";
    public const string PriceRemoved = "PriceRemoved";
    public const string PinChanged = "PinChanged";
    public const string OperatorUnlocked = "OperatorUnlocked";
    public const string DeviceKeyIssued = "DeviceKeyIssued";
    public const string DeviceKeyRevoked = "DeviceKeyRevoked";
    public const string MenuPublished = "MenuPublished";
    public const string MenuUnpublished = "MenuUnpublished";
    public const string SubsidyRuleClosed = "SubsidyRuleClosed";
    public const string BadgeIssued = "BadgeIssued";
    public const string BadgeLost = "BadgeLost";
    public const string BadgeBlocked = "BadgeBlocked";
    public const string BadgeUnblocked = "BadgeUnblocked";
    public const string AccountTopUp = "AccountTopUp";
    public const string AccountCorrection = "AccountCorrection";
    public const string AccountReversal = "AccountReversal";
    public const string AccountRefund = "AccountRefund";
    public const string DinersImported = "DinersImported";
    public const string ScopeGranted = "ScopeGranted";
    public const string ScopeRevoked = "ScopeRevoked";
}
