using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Application.Catalog;

/// <summary>
/// Master catalogue (central, administrators only), price lists (managers within their scope)
/// and effective price resolution for a point of sale.
/// </summary>
public sealed class CatalogService(IPosDbContext db, AccessControl access, AuditTrail audit, IFileStorage storage)
{
    public const long MaxPhotoBytes = 5 * 1024 * 1024;
    private static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    // ----- Categories ------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.DisplayOrder).ThenBy(c => c.Code).ToListAsync(ct);
        return [.. categories.Select(c => c.ToDto())];
    }

    public async Task<CategoryDto> CreateCategoryAsync(CategoryUpsert request, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.Categories.AnyAsync(c => c.Code == code, ct))
        {
            throw new ConflictException($"Category code '{code}' is already used.");
        }

        var category = new Category(Guid.CreateVersion7(), request.Code!, request.Name, request.DisplayOrder, request.ColorHex.Clean())
        { IsActive = request.IsActive };
        db.Categories.Add(category);
        audit.Record(AuditActions.Created, nameof(Category), category.Id, after: request);
        await db.SaveChangesAsync(ct);
        return category.ToDto();
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid id, CategoryUpsert request, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Category", id);
        var before = category.ToDto();
        category.Name = Guard.NotBlank(request.Name, nameof(request.Name), 100);
        category.DisplayOrder = request.DisplayOrder;
        category.ColorHex = request.ColorHex.Clean();
        category.IsActive = request.IsActive;
        audit.Record(AuditActions.Updated, nameof(Category), id, before, category.ToDto());
        await db.SaveChangesAsync(ct);
        return category.ToDto();
    }

    // ----- Articles --------------------------------------------------------------------------------------------------

    public async Task<PagedResult<ArticleDto>> ListArticlesAsync(string? search = null, Guid? categoryId = null, bool? isActive = null,
        int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        (page, pageSize) = Paging.Normalize(page, pageSize);
        var query = db.Articles.AsNoTracking().Include(a => a.Photos).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(a => a.Code.Contains(term) || a.Name.Contains(term));
        }

        if (categoryId is { } cat)
        {
            query = query.Where(a => a.CategoryId == cat);
        }

        if (isActive is { } active)
        {
            query = query.Where(a => a.IsActive == active);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(a => a.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<ArticleDto>([.. items.Select(a => a.ToDto())], total, page, pageSize);
    }

    public async Task<ArticleDto> GetArticleAsync(Guid id, CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var article = await db.Articles.AsNoTracking().Include(a => a.Photos).SingleOrDefaultAsync(a => a.Id == id, ct)
                      ?? throw new NotFoundException("Article", id);
        return article.ToDto();
    }

    public async Task<ArticleDto> CreateArticleAsync(ArticleUpsert request, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.Articles.AnyAsync(a => a.Code == code, ct))
        {
            throw new ConflictException($"Article code '{code}' is already used.");
        }

        await EnsureCategoryAsync(request.CategoryId, ct);
        var article = new Article(Guid.CreateVersion7(), request.Code!, request.Name, request.CategoryId, request.BasePrice, request.VatRate);
        Apply(article, request);
        db.Articles.Add(article);
        audit.Record(AuditActions.Created, nameof(Article), article.Id, after: article.ToDto());
        await db.SaveChangesAsync(ct);
        return article.ToDto();
    }

    /// <summary>The code is immutable (printed on tickets, used by the vision models).</summary>
    public async Task<ArticleDto> UpdateArticleAsync(Guid id, ArticleUpsert request, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var article = await db.Articles.Include(a => a.Photos).SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Article", id);
        if (!string.Equals(request.Code?.Trim(), article.Code, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("immutable_field", "An article code cannot change; create a new article instead.");
        }

        await EnsureCategoryAsync(request.CategoryId, ct);
        var before = article.ToDto();
        var priceChanged = article.BasePrice != request.BasePrice || article.VatRate != request.VatRate;
        article.Name = Guard.NotBlank(request.Name, nameof(request.Name), 150);
        article.CategoryId = request.CategoryId;
        article.SetBasePrice(request.BasePrice);
        article.SetVatRate(request.VatRate);
        Apply(article, request);
        audit.Record(priceChanged ? AuditActions.PriceChanged : AuditActions.Updated, nameof(Article), id, before, article.ToDto());
        await db.SaveChangesAsync(ct);
        return article.ToDto();
    }

    private static void Apply(Article article, ArticleUpsert request)
    {
        article.ReceiptLabel = request.ReceiptLabel.Clean();
        article.VisualDescription = request.VisualDescription.Clean();
        article.IsSubsidizable = request.IsSubsidizable;
        article.IsActive = request.IsActive;
    }

    private async Task EnsureCategoryAsync(Guid categoryId, CancellationToken ct)
    {
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId, ct))
        {
            throw new NotFoundException("Category", categoryId);
        }
    }

    public async Task<ArticlePhotoDto> AddPhotoAsync(Guid articleId, string fileName, Stream content, long length, string? caption,
        CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!PhotoExtensions.Contains(extension))
        {
            throw new DomainException("invalid_file", "Reference photos must be JPEG, PNG or WebP images.");
        }

        if (length is <= 0 or > MaxPhotoBytes)
        {
            throw new DomainException("invalid_file", $"Reference photos must be between 1 byte and {MaxPhotoBytes / 1024 / 1024} MB.");
        }

        var article = await db.Articles.Include(a => a.Photos).SingleOrDefaultAsync(a => a.Id == articleId, ct)
                      ?? throw new NotFoundException("Article", articleId);
        var key = $"articles/{article.Code}/{Guid.CreateVersion7():N}{extension}";
        await storage.SaveAsync(key, content, ct);
        var photo = article.AddPhoto(key, caption.Clean());
        db.ArticlePhotos.Add(photo);
        audit.Record(AuditActions.Updated, nameof(Article), articleId, after: new { PhotoAdded = key });
        await db.SaveChangesAsync(ct);
        return new ArticlePhotoDto(photo.Id, photo.StoragePath, photo.Caption, photo.DisplayOrder);
    }

    public async Task DeletePhotoAsync(Guid articleId, Guid photoId, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var photo = await db.ArticlePhotos.SingleOrDefaultAsync(p => p.Id == photoId && p.ArticleId == articleId, ct)
                    ?? throw new NotFoundException("ArticlePhoto", photoId);
        _ = await db.Articles.SingleAsync(a => a.Id == articleId, ct); // tracked: its row version moves, registers re-sync the photo list
        db.ArticlePhotos.Remove(photo);
        audit.Record(AuditActions.Updated, nameof(Article), articleId, before: new { PhotoRemoved = photo.StoragePath });
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(photo.StoragePath, ct);
    }

    public async Task<PhotoContent?> OpenPhotoAsync(Guid photoId, CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        return await ReadPhotoAsync(photoId, ct);
    }

    /// <summary>Reference photo downloaded by a register (authorised by the register token at the endpoint).</summary>
    public Task<PhotoContent?> OpenPhotoForRegisterAsync(Guid photoId, CancellationToken ct = default) => ReadPhotoAsync(photoId, ct);

    private async Task<PhotoContent?> ReadPhotoAsync(Guid photoId, CancellationToken ct)
    {
        var path = await db.ArticlePhotos.AsNoTracking().Where(p => p.Id == photoId).Select(p => p.StoragePath).SingleOrDefaultAsync(ct);
        if (path is null || await storage.OpenReadAsync(path, ct) is not { } stream)
        {
            return null;
        }

        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
        return new PhotoContent(stream, contentType);
    }

    // ----- Price lists -----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PriceListDto>> ListPriceListsAsync(Guid? companyId = null, CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        var lists = await db.PriceLists.AsNoTracking().Include(p => p.Overrides)
            .Where(p => companyId == null || p.CompanyId == companyId)
            .OrderBy(p => p.Code).ToListAsync(ct);
        var visible = lists.Where(l => l.SiteId is { } s ? scope.CanAccessSite(l.CompanyId, s) : scope.CanReadCompany(l.CompanyId)).ToList();
        return await ToDtosAsync(visible, ct);
    }

    public async Task<PriceListDto> GetPriceListAsync(Guid id, CancellationToken ct = default)
    {
        var list = await db.PriceLists.AsNoTracking().Include(p => p.Overrides).SingleOrDefaultAsync(p => p.Id == id, ct)
                   ?? throw new NotFoundException("PriceList", id);
        await EnsurePriceListScopeAsync(list.CompanyId, list.SiteId, list.PointOfSaleId, write: false, ct);
        return (await ToDtosAsync([list], ct))[0];
    }

    public async Task<PriceListDto> CreatePriceListAsync(PriceListCreate request, CancellationToken ct = default)
    {
        await EnsurePriceListScopeAsync(request.CompanyId, request.SiteId, request.PointOfSaleId, write: true, ct);
        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.PriceLists.AnyAsync(p => p.CompanyId == request.CompanyId && p.Code == code, ct))
        {
            throw new ConflictException($"Price list code '{code}' is already used in this company.");
        }

        var list = new PriceList(Guid.CreateVersion7(), request.Code!, request.Name, request.CompanyId, request.ValidFrom, request.ValidTo,
            request.SiteId, request.PointOfSaleId);
        db.PriceLists.Add(list);
        audit.Record(AuditActions.Created, nameof(PriceList), list.Id, after: request, companyId: request.CompanyId, siteId: request.SiteId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([list], ct))[0];
    }

    public async Task<PriceListDto> UpdatePriceListAsync(Guid id, PriceListUpdate request, CancellationToken ct = default)
    {
        var list = await db.PriceLists.Include(p => p.Overrides).SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("PriceList", id);
        await EnsurePriceListScopeAsync(list.CompanyId, list.SiteId, list.PointOfSaleId, write: true, ct);
        var before = new { list.Name, list.ValidFrom, list.ValidTo, list.IsActive };
        list.Name = Guard.NotBlank(request.Name, nameof(request.Name));
        list.SetValidity(request.ValidFrom, request.ValidTo);
        list.IsActive = request.IsActive;
        audit.Record(AuditActions.PriceChanged, nameof(PriceList), id, before, request, list.CompanyId, list.SiteId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([list], ct))[0];
    }

    public async Task<PriceListDto> SetPriceAsync(Guid priceListId, Guid articleId, decimal price, CancellationToken ct = default)
    {
        var list = await db.PriceLists.Include(p => p.Overrides).SingleOrDefaultAsync(p => p.Id == priceListId, ct)
                   ?? throw new NotFoundException("PriceList", priceListId);
        await EnsurePriceListScopeAsync(list.CompanyId, list.SiteId, list.PointOfSaleId, write: true, ct);
        var article = await db.Articles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == articleId, ct) ?? throw new NotFoundException("Article", articleId);
        var previous = list.Overrides.FirstOrDefault(o => o.ArticleId == articleId)?.Price;
        list.SetPrice(articleId, price);
        audit.Record(AuditActions.PriceChanged, nameof(PriceList), priceListId,
            new { Article = article.Code, Price = previous }, new { Article = article.Code, Price = price }, list.CompanyId, list.SiteId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([list], ct))[0];
    }

    public async Task<PriceListDto> RemovePriceAsync(Guid priceListId, Guid articleId, CancellationToken ct = default)
    {
        var list = await db.PriceLists.Include(p => p.Overrides).SingleOrDefaultAsync(p => p.Id == priceListId, ct)
                   ?? throw new NotFoundException("PriceList", priceListId);
        await EnsurePriceListScopeAsync(list.CompanyId, list.SiteId, list.PointOfSaleId, write: true, ct);
        var previous = list.Overrides.FirstOrDefault(o => o.ArticleId == articleId)?.Price;
        if (!list.RemovePrice(articleId))
        {
            throw new NotFoundException("PriceOverride", articleId);
        }

        audit.Record(AuditActions.PriceRemoved, nameof(PriceList), priceListId, new { ArticleId = articleId, Price = previous }, null,
            list.CompanyId, list.SiteId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([list], ct))[0];
    }

    /// <summary>Effective prices of every active article for a point of sale on a date.</summary>
    public async Task<IReadOnlyList<EffectivePriceDto>> GetEffectivePricesAsync(Guid pointOfSaleId, DateOnly date, CancellationToken ct = default)
    {
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(pointOfSaleId, write: false, ct);
        var resolver = await CreateResolverAsync(companyId, siteId, pointOfSaleId, date, ct);
        var articles = await db.Articles.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Code).ToListAsync(ct);
        return [.. articles.Select(a => new EffectivePriceDto(a.Id, a.Code, a.Name, a.BasePrice, resolver(a)))];
    }

    /// <summary>Price resolver for one point of sale and date (used by menus).</summary>
    internal async Task<Func<Article, decimal>> CreateResolverAsync(Guid companyId, Guid siteId, Guid pointOfSaleId, DateOnly date, CancellationToken ct)
    {
        var lists = await db.PriceLists.AsNoTracking().Include(p => p.Overrides)
            .Where(p => p.CompanyId == companyId && p.IsActive && p.ValidFrom <= date && (p.ValidTo == null || p.ValidTo >= date))
            .ToListAsync(ct);
        var context = new PriceContext(companyId, siteId, pointOfSaleId);
        return article => PriceResolver.Resolve(article, lists, context, date);
    }

    private async Task EnsurePriceListScopeAsync(Guid companyId, Guid? siteId, Guid? pointOfSaleId, bool write, CancellationToken ct)
    {
        if (pointOfSaleId is { } pos)
        {
            var (posCompany, posSite) = await access.EnsureCanAccessPointOfSaleAsync(pos, write, ct);
            if (posCompany != companyId || (siteId is { } s && s != posSite))
            {
                throw new DomainException("invalid_scope", "The point of sale does not belong to the given site/company.");
            }
        }
        else if (siteId is { } site)
        {
            if (await access.EnsureCanAccessSiteAsync(site, write, ct) != companyId)
            {
                throw new DomainException("invalid_scope", "The site does not belong to the company.");
            }
        }
        else if (write)
        {
            await access.EnsureCanManageCompanyAsync(companyId, ct);
        }
        else
        {
            await access.EnsureCanReadCompanyAsync(companyId, ct);
        }
    }

    private async Task<List<PriceListDto>> ToDtosAsync(IReadOnlyCollection<PriceList> lists, CancellationToken ct)
    {
        var articleIds = lists.SelectMany(l => l.Overrides.Select(o => o.ArticleId)).Distinct().ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => (a.Code, a.Name), ct);
        return [.. lists.Select(l => new PriceListDto(l.Id, l.Code, l.Name, l.CompanyId, l.SiteId, l.PointOfSaleId, l.ValidFrom, l.ValidTo,
            l.IsActive, [.. l.Overrides
                .Select(o => new PriceOverrideDto(o.ArticleId, articles.GetValueOrDefault(o.ArticleId).Code ?? "?",
                    articles.GetValueOrDefault(o.ArticleId).Name ?? "?", o.Price))
                .OrderBy(o => o.ArticleCode)]))];
    }
}

public sealed record PhotoContent(Stream Content, string ContentType);

internal static class Paging
{
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Normalize(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
}
