using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Catalog;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Menus;

namespace Newrest.Pos.Application.Menus;

/// <summary>Daily menus per point of sale. Prices are resolved from price lists when an item is added, then frozen.</summary>
public sealed class MenuService(IPosDbContext db, AccessControl access, AuditTrail audit, CatalogService catalog)
{
    public async Task<IReadOnlyList<DailyMenuDto>> ListAsync(Guid pointOfSaleId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await access.EnsureCanAccessPointOfSaleAsync(pointOfSaleId, write: false, ct);
        if (to < from || to.DayNumber - from.DayNumber > 62)
        {
            throw new DomainException("invalid_range", "The date range must be ordered and at most 62 days.");
        }

        var menus = await db.DailyMenus.AsNoTracking().Include(m => m.Items)
            .Where(m => m.PointOfSaleId == pointOfSaleId && m.Date >= from && m.Date <= to)
            .OrderBy(m => m.Date).ThenBy(m => m.Service)
            .ToListAsync(ct);
        return await ToDtosAsync(menus, ct);
    }

    public async Task<DailyMenuDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var menu = await LoadAsync(id, tracked: false, ct);
        await access.EnsureCanAccessPointOfSaleAsync(menu.PointOfSaleId, write: false, ct);
        return (await ToDtosAsync([menu], ct))[0];
    }

    public async Task<DailyMenuDto> CreateAsync(DailyMenuCreate request, CancellationToken ct = default)
    {
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(request.PointOfSaleId, write: true, ct);
        var service = Mappings.Parse<MealService>(request.Service, "service");
        if (await db.DailyMenus.AnyAsync(m => m.PointOfSaleId == request.PointOfSaleId && m.Date == request.Date && m.Service == service, ct))
        {
            throw new ConflictException($"A {service} menu already exists for {request.Date:yyyy-MM-dd}.");
        }

        var menu = new DailyMenu(Guid.CreateVersion7(), request.PointOfSaleId, request.Date, service);
        var ids = (request.ArticleIds ?? []).Distinct().ToList();
        if (ids.Count > 0)
        {
            var resolve = await catalog.CreateResolverAsync(companyId, siteId, request.PointOfSaleId, request.Date, ct);
            var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            foreach (var id in ids)
            {
                var article = articles.GetValueOrDefault(id) ?? throw new NotFoundException("Article", id);
                EnsureActive(article);
                menu.AddItem(id, resolve(article));
            }
        }

        db.DailyMenus.Add(menu);
        audit.Record(AuditActions.Created, nameof(DailyMenu), menu.Id, after: request, companyId: companyId, siteId: siteId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([menu], ct))[0];
    }

    public async Task<DailyMenuDto> AddItemAsync(Guid menuId, MenuItemAdd request, CancellationToken ct = default)
    {
        var menu = await LoadAsync(menuId, tracked: true, ct);
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(menu.PointOfSaleId, write: true, ct);
        var article = await db.Articles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.ArticleId, ct)
                      ?? throw new NotFoundException("Article", request.ArticleId);
        EnsureActive(article);
        var price = request.Price ?? (await catalog.CreateResolverAsync(companyId, siteId, menu.PointOfSaleId, menu.Date, ct))(article);
        menu.AddItem(article.Id, price);
        if (request.Price is not null)
        {
            audit.Record(AuditActions.PriceChanged, nameof(DailyMenu), menuId, null, new { Article = article.Code, Price = price }, companyId, siteId);
        }

        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([menu], ct))[0];
    }

    public async Task<DailyMenuDto> UpdateItemAsync(Guid menuId, Guid articleId, MenuItemUpdate request, CancellationToken ct = default)
    {
        var menu = await LoadAsync(menuId, tracked: true, ct);
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(menu.PointOfSaleId, write: true, ct);
        var item = menu.Items.FirstOrDefault(i => i.ArticleId == articleId) ?? throw new NotFoundException("DailyMenuItem", articleId);
        if (item.EffectivePrice != request.Price)
        {
            audit.Record(AuditActions.PriceChanged, nameof(DailyMenu), menuId, new { ArticleId = articleId, Price = item.EffectivePrice },
                new { ArticleId = articleId, request.Price }, companyId, siteId);
            menu.SetItemPrice(articleId, request.Price);
        }

        menu.SetAvailability(articleId, request.IsAvailable);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([menu], ct))[0];
    }

    public async Task<DailyMenuDto> RemoveItemAsync(Guid menuId, Guid articleId, CancellationToken ct = default)
    {
        var menu = await LoadAsync(menuId, tracked: true, ct);
        await access.EnsureCanAccessPointOfSaleAsync(menu.PointOfSaleId, write: true, ct);
        menu.RemoveItem(articleId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([menu], ct))[0];
    }

    public async Task<DailyMenuDto> SetPublishedAsync(Guid menuId, bool published, CancellationToken ct = default)
    {
        var menu = await LoadAsync(menuId, tracked: true, ct);
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(menu.PointOfSaleId, write: true, ct);
        if (published)
        {
            menu.Publish();
        }
        else
        {
            menu.Unpublish();
        }

        audit.Record(published ? AuditActions.MenuPublished : AuditActions.MenuUnpublished, nameof(DailyMenu), menuId,
            companyId: companyId, siteId: siteId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([menu], ct))[0];
    }

    /// <summary>Deletes a draft menu. Published menus must be unpublished first.</summary>
    public async Task DeleteAsync(Guid menuId, CancellationToken ct = default)
    {
        var menu = await LoadAsync(menuId, tracked: true, ct);
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(menu.PointOfSaleId, write: true, ct);
        if (menu.IsPublished)
        {
            throw new DomainException("menu_published", "Unpublish the menu before deleting it.");
        }

        db.DailyMenus.Remove(menu);
        audit.Record(AuditActions.Deleted, nameof(DailyMenu), menuId, before: new { menu.Date, menu.Service }, companyId: companyId, siteId: siteId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MenuCopyResult> CopyAsync(Guid sourceMenuId, MenuCopyRequest request, CancellationToken ct = default)
    {
        var source = await LoadAsync(sourceMenuId, tracked: false, ct);
        await access.EnsureCanAccessPointOfSaleAsync(source.PointOfSaleId, write: false, ct);
        var targetPos = request.TargetPointOfSaleId ?? source.PointOfSaleId;
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(targetPos, write: true, ct);
        var dates = (request.TargetDates ?? []).Distinct().ToList();
        if (dates.Count is 0 or > 31)
        {
            throw new DomainException("invalid_range", "Provide between 1 and 31 target dates.");
        }

        var result = await CopyManyAsync([(source, dates)], targetPos, request.Overwrite, ct);
        audit.Record(AuditActions.Created, nameof(DailyMenu), sourceMenuId, after: new { Copy = request, result.Created, result.Replaced }, companyId: companyId, siteId: siteId);
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Copies a template week (7 days from <see cref="MenuWeekCopyRequest.SourceWeekStart"/>) to another week.</summary>
    public async Task<MenuCopyResult> CopyWeekAsync(MenuWeekCopyRequest request, CancellationToken ct = default)
    {
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(request.PointOfSaleId, write: true, ct);
        var sourceEnd = request.SourceWeekStart.AddDays(6);
        var sources = await db.DailyMenus.AsNoTracking().Include(m => m.Items)
            .Where(m => m.PointOfSaleId == request.PointOfSaleId && m.Date >= request.SourceWeekStart && m.Date <= sourceEnd)
            .ToListAsync(ct);
        var offset = request.TargetWeekStart.DayNumber - request.SourceWeekStart.DayNumber;
        if (offset == 0)
        {
            throw new DomainException("invalid_range", "Source and target weeks are the same.");
        }

        var result = await CopyManyAsync([.. sources.Select(s => (s, new List<DateOnly> { s.Date.AddDays(offset) }))],
            request.PointOfSaleId, request.Overwrite, ct);
        audit.Record(AuditActions.Created, nameof(DailyMenu), request.PointOfSaleId, after: new { CopyWeek = request, result.Created, result.Replaced },
            companyId: companyId, siteId: siteId);
        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<MenuCopyResult> CopyManyAsync(IReadOnlyList<(DailyMenu Source, List<DateOnly> Dates)> work, Guid targetPos, bool overwrite,
        CancellationToken ct)
    {
        int created = 0, replaced = 0, skipped = 0;
        var ids = new List<Guid>();
        var allDates = work.SelectMany(w => w.Dates).ToList();
        var existing = await db.DailyMenus.Include(m => m.Items)
            .Where(m => m.PointOfSaleId == targetPos && allDates.Contains(m.Date))
            .ToListAsync(ct);

        foreach (var (source, dates) in work)
        {
            foreach (var date in dates)
            {
                var current = existing.FirstOrDefault(m => m.Date == date && m.Service == source.Service);
                if (current is not null)
                {
                    if (!overwrite || current.IsPublished)
                    {
                        skipped++;
                        continue;
                    }

                    db.DailyMenus.Remove(current);
                    replaced++;
                }
                else
                {
                    created++;
                }

                var copy = source.CopyTo(date, pointOfSaleId: targetPos);
                db.DailyMenus.Add(copy);
                ids.Add(copy.Id);
            }
        }

        return new MenuCopyResult(created, replaced, skipped, ids);
    }

    private async Task<DailyMenu> LoadAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var query = db.DailyMenus.Include(m => m.Items).AsQueryable();
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("DailyMenu", id);
    }

    private static void EnsureActive(Domain.Catalog.Article article)
    {
        if (!article.IsActive)
        {
            throw new DomainException("article_inactive", $"Article {article.Code} is inactive.");
        }
    }

    private async Task<List<DailyMenuDto>> ToDtosAsync(IReadOnlyCollection<DailyMenu> menus, CancellationToken ct)
    {
        var ids = menus.SelectMany(m => m.Items.Select(i => i.ArticleId)).Distinct().ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => (a.Code, a.Name, a.CategoryId), ct);
        return [.. menus.Select(m => new DailyMenuDto(m.Id, m.PointOfSaleId, m.Date, m.Service.ToString(), m.IsPublished,
            [.. m.Items.OrderBy(i => i.DisplayOrder).Select(i =>
            {
                var a = articles.GetValueOrDefault(i.ArticleId);
                return new DailyMenuItemDto(i.ArticleId, a.Code ?? "?", a.Name ?? "?", a.CategoryId, i.EffectivePrice, i.IsAvailable, i.DisplayOrder);
            })]))];
    }
}
