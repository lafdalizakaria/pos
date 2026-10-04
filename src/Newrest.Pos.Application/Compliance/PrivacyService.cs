using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Application.Compliance;

/// <summary>
/// Data minimisation (law 09-08): diners who left (inactive, no movement since the cut-off, balance at zero) are
/// anonymised. Fiscal tickets are immutable and keep their reference to the (now anonymous) diner.
/// </summary>
public sealed class PrivacyService(IPosDbContext db, AccessControl access, AuditTrail audit, TimeProvider clock)
{
    public async Task<AnonymizationResult> AnonymizeInactiveDinersAsync(AnonymizationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        access.RequireAnyRole(PosRoles.Admin);
        var cutoff = new DateTimeOffset(request.InactiveBefore.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        if (cutoff > clock.GetUtcNow().AddMonths(-3))
        {
            throw new DomainException("cutoff_too_recent", "The cut-off date must be at least 3 months ago (invoicing and disputes).");
        }

        var candidates = await db.Diners
            .Where(d => !d.IsActive && d.UpdatedAt < cutoff && !d.EmployeeNumber.StartsWith(Diner.AnonymizedPrefix))
            .Where(d => !db.Accounts.Any(a => a.DinerId == d.Id && (a.CachedBalance != 0 || a.IsActive)))
            .Where(d => !(from a in db.Accounts
                          join m in db.AccountMovements on a.Id equals m.AccountId
                          where a.DinerId == d.Id && m.OccurredAt >= cutoff
                          select m.Id).Any())
            .ToListAsync(ct);
        var ids = candidates.Select(d => d.Id).ToList();
        var badges = await db.Badges.Where(b => ids.Contains(b.DinerId)).ToListAsync(ct);
        if (request.DryRun)
        {
            return new AnonymizationResult(candidates.Count, badges.Count, true);
        }

        foreach (var diner in candidates)
        {
            diner.Anonymize();
        }

        foreach (var badge in badges)
        {
            badge.Anonymize();
        }

        audit.Record(AuditActions.Updated, nameof(Diner), "anonymization",
            after: new { Cutoff = request.InactiveBefore, Diners = candidates.Count, Badges = badges.Count });
        await db.SaveChangesAsync(ct);
        return new AnonymizationResult(candidates.Count, badges.Count, false);
    }
}
