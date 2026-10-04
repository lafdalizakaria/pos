using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Application.Auditing;

/// <summary>Audit trail consultation (administrators and finance). Non-global users only see their companies.</summary>
public sealed class AuditQueryService(IPosDbContext db, AccessControl access)
{
    public async Task<PagedResult<AuditEntryDto>> SearchAsync(string? entityType = null, string? entityId = null, string? actor = null,
        string? action = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        access.RequireAnyRole(AccessControl.Finance);
        var scope = await access.GetScopeAsync(ct);
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!scope.IsGlobal)
        {
            var companies = scope.CompanyIds.ToList();
            query = query.Where(a => a.CompanyId != null && companies.Contains(a.CompanyId.Value));
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        if (!string.IsNullOrWhiteSpace(actor))
        {
            query = query.Where(a => a.Actor.Contains(actor));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        if (from is { } f)
        {
            query = query.Where(a => a.OccurredAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(a => a.OccurredAt <= t);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.OccurredAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AuditEntryDto>([.. items.Select(a => a.ToDto())], total, page, pageSize);
    }
}
