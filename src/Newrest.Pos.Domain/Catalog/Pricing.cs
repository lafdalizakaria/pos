using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Catalog;

/// <summary>
/// A set of price overrides valid for a period and a scope. Scope precedence (most specific wins):
/// point of sale &gt; site &gt; company. Articles without an applicable override use <see cref="Article.BasePrice"/>.
/// </summary>
public sealed class PriceList : ReferenceEntity
{
    private readonly List<PriceOverride> _overrides = [];

    private PriceList()
    {
    }

    public PriceList(Guid id, string code, string name, Guid companyId, DateOnly validFrom, DateOnly? validTo = null,
        Guid? siteId = null, Guid? pointOfSaleId = null) : base(id)
    {
        Code = Guard.NotBlank(code, nameof(code), 32).ToUpperInvariant();
        Name = Guard.NotBlank(name, nameof(name));
        CompanyId = Guard.NotEmpty(companyId, nameof(companyId));
        SiteId = siteId;
        PointOfSaleId = pointOfSaleId;
        SetValidity(validFrom, validTo);
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public string Name { get; set; } = null!;
    public Guid CompanyId { get; private set; }
    public Guid? SiteId { get; private set; }
    public Guid? PointOfSaleId { get; private set; }
    public DateOnly ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public bool IsActive { get; set; }

    public IReadOnlyCollection<PriceOverride> Overrides => _overrides;

    public PriceScope Scope => PointOfSaleId is not null ? PriceScope.PointOfSale : SiteId is not null ? PriceScope.Site : PriceScope.Company;

    public void SetValidity(DateOnly validFrom, DateOnly? validTo)
    {
        if (validTo is { } to && to < validFrom)
        {
            throw new DomainException("invalid_validity", "ValidTo must be on or after ValidFrom.");
        }

        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public bool IsValidOn(DateOnly date) => IsActive && date >= ValidFrom && (ValidTo is null || date <= ValidTo);

    public bool AppliesTo(PriceContext context) =>
        CompanyId == context.CompanyId
        && (SiteId is null || SiteId == context.SiteId)
        && (PointOfSaleId is null || PointOfSaleId == context.PointOfSaleId);

    public PriceOverride SetPrice(Guid articleId, decimal price)
    {
        Guard.NotNegative(price, nameof(price));
        Money.EnsureValid(price, nameof(price));
        var existing = _overrides.FirstOrDefault(o => o.ArticleId == articleId);
        if (existing is not null)
        {
            existing.Price = price;
            return existing;
        }

        var created = new PriceOverride(Id, articleId, price);
        _overrides.Add(created);
        return created;
    }
}

public enum PriceScope
{
    Company = 1,
    Site = 2,
    PointOfSale = 3,
}

public sealed class PriceOverride : Entity
{
    private PriceOverride()
    {
    }

    internal PriceOverride(Guid priceListId, Guid articleId, decimal price)
    {
        PriceListId = priceListId;
        ArticleId = Guard.NotEmpty(articleId, nameof(articleId));
        Price = price;
    }

    public Guid PriceListId { get; private set; }
    public Guid ArticleId { get; private set; }
    public decimal Price { get; internal set; }
}

public readonly record struct PriceContext(Guid CompanyId, Guid SiteId, Guid PointOfSaleId);

public static class PriceResolver
{
    /// <summary>
    /// Effective price of an article. Among applicable and valid lists, the most specific scope wins;
    /// on a tie, the most recent <see cref="PriceList.ValidFrom"/> wins.
    /// </summary>
    public static decimal Resolve(Article article, IEnumerable<PriceList> priceLists, PriceContext context, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(priceLists);

        var match = priceLists
            .Where(pl => pl.IsValidOn(date) && pl.AppliesTo(context))
            .Select(pl => (List: pl, Override: pl.Overrides.FirstOrDefault(o => o.ArticleId == article.Id)))
            .Where(x => x.Override is not null)
            .OrderByDescending(x => x.List.Scope)
            .ThenByDescending(x => x.List.ValidFrom)
            .FirstOrDefault();

        return match.Override?.Price ?? article.BasePrice;
    }
}
