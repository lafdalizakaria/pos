using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Application.Sync;

/// <summary>
/// Data sent down to a register: its profile, an incremental reference snapshot (catalogue, operators, contracts,
/// diners, badges, balances, menus) and online badge lookups.
/// </summary>
public sealed class RegisterReferenceService(IPosDbContext db, IRowVersionSource rowVersions, TimeProvider clock)
{
    /// <summary>A cursor older than this triggers a full snapshot (catches scope removals that deltas cannot express).</summary>
    public static readonly TimeSpan FullSyncInterval = TimeSpan.FromHours(24);

    public async Task<RegisterProfileDto> GetProfileAsync(Guid registerId, CancellationToken ct = default)
    {
        var p = await (from r in db.Registers.AsNoTracking()
                       join pos in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals pos.Id
                       join s in db.Sites.AsNoTracking() on pos.SiteId equals s.Id
                       join c in db.Companies.AsNoTracking() on s.CompanyId equals c.Id
                       where r.Id == registerId
                       select new { r, pos, s, c }).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Register", registerId);
        return new RegisterProfileDto(p.r.Id, p.r.Code, p.r.Name, p.r.TicketPrefix, p.pos.Id, p.pos.Name, p.s.Id, p.s.Name, p.s.Address, p.s.TimeZone,
            p.c.Id, p.c.Name, p.c.LegalName, p.c.Ice, p.c.TaxId, p.c.TradeRegister, p.c.Address,
            p.r.LastSyncedTicketSequence, p.r.LastSyncedTicketHash, p.r.LastZNumber);
    }

    public async Task<ReferenceSyncResponse> GetReferenceAsync(Guid registerId, string? cursor, CancellationToken ct = default)
    {
        var scope = await LoadScopeAsync(registerId, ct);
        var now = clock.GetUtcNow();
        var upper = await rowVersions.GetMinActiveRowVersionAsync(ct);
        var since = Cursor.TryParse(cursor, now) ?? 0UL;

        // Contract changes may add or remove whole populations of diners: answer with a full snapshot.
        if (since > 0 && await db.Contracts.AnyAsync(c => c.RowVersion >= since && c.RowVersion < upper
                                                          && db.ClientCompanies.Any(cc => cc.Id == c.ClientCompanyId && cc.CompanyId == scope.CompanyId), ct))
        {
            since = 0;
        }

        var full = since == 0;
        bool Changed(ulong rv) => full || (rv >= since && rv < upper);

        var categories = await db.Categories.AsNoTracking().Where(c => full || (c.RowVersion >= since && c.RowVersion < upper)).ToListAsync(ct);
        var articles = await db.Articles.AsNoTracking().Where(a => full || (a.RowVersion >= since && a.RowVersion < upper)).ToListAsync(ct);
        var operators = await db.Operators.AsNoTracking()
            .Where(o => o.CompanyId == scope.CompanyId && (o.SiteId == null || o.SiteId == scope.SiteId))
            .Where(o => full || (o.RowVersion >= since && o.RowVersion < upper)).ToListAsync(ct);

        var contracts = await (from c in db.Contracts.AsNoTracking().Include(c => c.PointsOfSale)
                               join cc in db.ClientCompanies.AsNoTracking() on c.ClientCompanyId equals cc.Id
                               where cc.CompanyId == scope.CompanyId
                               select c).ToListAsync(ct);
        var accepting = contracts.Where(c => c.AcceptsPointOfSale(scope.PointOfSaleId)).ToList();
        var acceptingIds = accepting.Select(c => c.Id).ToList();
        var clientIds = accepting.Select(c => c.ClientCompanyId).Distinct().ToList();

        var rules = await db.SubsidyRules.AsNoTracking()
            .Where(r => acceptingIds.Contains(r.ContractId) && (full || (r.RowVersion >= since && r.RowVersion < upper))).ToListAsync(ct);
        var diners = await db.Diners.AsNoTracking()
            .Where(d => clientIds.Contains(d.ClientCompanyId) && (full || (d.RowVersion >= since && d.RowVersion < upper))).ToListAsync(ct);
        var badges = await (from b in db.Badges.AsNoTracking()
                            join d in db.Diners.AsNoTracking() on b.DinerId equals d.Id
                            where clientIds.Contains(d.ClientCompanyId) && (full || (b.RowVersion >= since && b.RowVersion < upper))
                            select b).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => acceptingIds.Contains(a.ContractId) && (full || (a.RowVersion >= since && a.RowVersion < upper))).ToListAsync(ct);

        var today = scope.Today(now);
        var menus = await db.DailyMenus.AsNoTracking().Include(m => m.Items)
            .Where(m => m.PointOfSaleId == scope.PointOfSaleId && m.IsPublished && m.Date >= today.AddDays(-1) && m.Date <= today.AddDays(1))
            .ToListAsync(ct);
        var menuArticles = menus.SelectMany(m => m.Items.Select(i => i.ArticleId)).Distinct().ToList();
        var articleInfo = await db.Articles.AsNoTracking().Where(a => menuArticles.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => (a.Code, a.Name, a.CategoryId), ct);

        return new ReferenceSyncResponse(
            Cursor.Format(upper, now), full, now,
            [.. categories.Select(c => new SyncCategoryDto(c.Id, c.Code, c.Name, c.DisplayOrder, c.ColorHex, c.IsActive))],
            [.. articles.Select(a => new SyncArticleDto(a.Id, a.Code, a.Name, a.ReceiptLabel, a.CategoryId, a.BasePrice, a.VatRate, a.IsSubsidizable, a.IsActive))],
            [.. operators.Select(o => new SyncOperatorDto(o.Id, o.SiteId, o.Code, o.FirstName, o.LastName, (int)o.Roles, o.PinHash, o.IsActive))],
            [.. contracts.Where(c => full || Changed(c.RowVersion))
                .Select(c => new SyncContractDto(c.Id, c.ClientCompanyId, c.StartDate, c.EndDate, c.IsActive, c.AcceptsPointOfSale(scope.PointOfSaleId)))],
            [.. rules.Select(r => r.ToDto())],
            [.. diners.Select(ToSync)],
            [.. badges.Select(ToSync)],
            [.. accounts.Select(ToSync)],
            [.. menus.OrderBy(m => m.Date).ThenBy(m => m.Service).Select(m => new DailyMenuDto(m.Id, m.PointOfSaleId, m.Date, m.Service.ToString(), m.IsPublished,
                [.. m.Items.OrderBy(i => i.DisplayOrder).Select(i =>
                {
                    var a = articleInfo.GetValueOrDefault(i.ArticleId);
                    return new DailyMenuItemDto(i.ArticleId, a.Code ?? "?", a.Name ?? "?", a.CategoryId, i.EffectivePrice, i.IsAvailable, i.DisplayOrder);
                })]))]);
    }

    /// <summary>Online badge lookup with the subsidy already granted to the diner on <paramref name="businessDate"/> on every point of sale.</summary>
    public async Task<BadgeContextDto> GetBadgeContextAsync(Guid registerId, string badgeNumber, DateOnly businessDate, CancellationToken ct = default)
    {
        var scope = await LoadScopeAsync(registerId, ct);
        var number = Badge.NormalizeNumber(badgeNumber);
        var badge = await db.Badges.AsNoTracking().SingleOrDefaultAsync(b => b.Number == number, ct) ?? throw new NotFoundException("Badge", number);
        var diner = await db.Diners.AsNoTracking().SingleAsync(d => d.Id == badge.DinerId, ct);
        var clientCompany = await db.ClientCompanies.AsNoTracking().SingleAsync(c => c.Id == diner.ClientCompanyId, ct);
        if (clientCompany.CompanyId != scope.CompanyId)
        {
            throw new NotFoundException("Badge", number);
        }

        var contracts = await db.Contracts.AsNoTracking().Include(c => c.PointsOfSale).Include(c => c.SubsidyRules)
            .Where(c => c.ClientCompanyId == diner.ClientCompanyId).ToListAsync(ct);
        var accepting = contracts.Where(c => c.AcceptsPointOfSale(scope.PointOfSaleId) && c.IsActiveOn(businessDate)).ToList();
        var acceptingIds = accepting.Select(c => c.Id).ToList();
        var account = await db.Accounts.AsNoTracking()
            .Where(a => a.DinerId == diner.Id && acceptingIds.Contains(a.ContractId))
            .OrderByDescending(a => a.IsActive).FirstOrDefaultAsync(ct);

        var today = await db.Tickets.AsNoTracking()
            .Where(t => t.DinerId == diner.Id && t.BusinessDate == businessDate && t.SubsidyAmount != 0)
            .Select(t => new { t.SubsidyAmount, t.Kind }).ToListAsync(ct);
        var meals = today.Count(t => t.Kind == TicketKind.Sale) - today.Count(t => t.Kind == TicketKind.CreditNote);

        return new BadgeContextDto(ToSync(diner), ToSync(badge), account is null ? null : ToSync(account), accepting.Count > 0,
            today.Sum(t => t.SubsidyAmount), Math.Max(0, meals),
            [.. accepting.Where(c => account is null || c.Id == account.ContractId).SelectMany(c => c.SubsidyRules)
                .Where(r => r.IsValidOn(businessDate)).Select(r => r.ToDto())]);
    }

    internal async Task<RegisterScope> LoadScopeAsync(Guid registerId, CancellationToken ct) =>
        await (from r in db.Registers.AsNoTracking()
               join p in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals p.Id
               join s in db.Sites.AsNoTracking() on p.SiteId equals s.Id
               where r.Id == registerId && r.IsActive
               select new RegisterScope(r.Id, r.TicketPrefix, p.Id, s.Id, s.CompanyId, s.TimeZone)).SingleOrDefaultAsync(ct)
        ?? throw new ForbiddenException("Unknown or inactive register.");

    private static SyncDinerDto ToSync(Diner d) => new(d.Id, d.ClientCompanyId, d.EmployeeNumber, d.FirstName, d.LastName, d.Category, d.IsActive);

    private static SyncBadgeDto ToSync(Badge b) => new(b.Id, b.DinerId, b.Number, b.Status.ToString());

    private static SyncAccountDto ToSync(Domain.Accounts.Account a) =>
        new(a.Id, a.DinerId, a.ContractId, a.Type.ToString(), a.OverdraftLimit, a.CachedBalance, a.IsActive);

    /// <summary>Opaque cursor: rowversion upper bound + issue time (for the periodic full snapshot).</summary>
    internal static class Cursor
    {
        public static string Format(ulong rowVersion, DateTimeOffset issuedAt) =>
            Convert.ToBase64String(Encoding.ASCII.GetBytes(FormattableString.Invariant($"v1:{rowVersion}:{issuedAt.ToUnixTimeSeconds()}")));

        public static ulong? TryParse(string? cursor, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(cursor))
            {
                return null;
            }

            try
            {
                var parts = Encoding.ASCII.GetString(Convert.FromBase64String(cursor)).Split(':');
                if (parts.Length != 3 || parts[0] != "v1"
                    || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rv)
                    || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var issued)
                    || now - DateTimeOffset.FromUnixTimeSeconds(issued) > FullSyncInterval)
                {
                    return null;
                }

                return rv;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}

internal sealed record RegisterScope(Guid RegisterId, string TicketPrefix, Guid PointOfSaleId, Guid SiteId, Guid CompanyId, string TimeZone)
{
    public DateOnly Today(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var tz) ? tz : TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }
}
