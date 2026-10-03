using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;

namespace Newrest.Pos.Application.Organization;

public sealed record AuthenticatedRegister(Guid RegisterId, string Code, string TicketPrefix, Guid PointOfSaleId, Guid SiteId, Guid CompanyId);

/// <summary>Validates a register's device key before a short-lived access token is issued by the API.</summary>
public sealed class RegisterAuthService(IPosDbContext db, TimeProvider clock)
{
    /// <returns>Null when the register is unknown, inactive, has no key or the key does not match (no detail leaked).</returns>
    public async Task<AuthenticatedRegister?> AuthenticateAsync(Guid registerId, string? deviceKey, CancellationToken ct = default)
    {
        var row = await (from r in db.Registers
                         join p in db.PointsOfSale on r.PointOfSaleId equals p.Id
                         join s in db.Sites on p.SiteId equals s.Id
                         where r.Id == registerId
                         select new { Register = r, PosActive = p.IsActive, SiteActive = s.IsActive, p.SiteId, s.CompanyId })
            .SingleOrDefaultAsync(ct);

        if (row is null || !row.Register.IsActive || !row.PosActive || !row.SiteActive
            || !DeviceKeys.Matches(deviceKey, row.Register.DeviceKeyHash))
        {
            return null;
        }

        row.Register.LastSeenAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new AuthenticatedRegister(row.Register.Id, row.Register.Code, row.Register.TicketPrefix, row.Register.PointOfSaleId,
            row.SiteId, row.CompanyId);
    }
}
