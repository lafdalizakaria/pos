using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Menus;

public enum MealService
{
    Breakfast,
    Lunch,
    Dinner,
    AllDay,
}

/// <summary>Menu of a point of sale for one date and service. Its items are the vision candidates of the day.</summary>
public sealed class DailyMenu : ReferenceEntity
{
    private readonly List<DailyMenuItem> _items = [];

    private DailyMenu()
    {
    }

    public DailyMenu(Guid id, Guid pointOfSaleId, DateOnly date, MealService service) : base(id)
    {
        PointOfSaleId = Guard.NotEmpty(pointOfSaleId, nameof(pointOfSaleId));
        Date = date;
        Service = service;
    }

    public Guid PointOfSaleId { get; private set; }
    public DateOnly Date { get; private set; }
    public MealService Service { get; private set; }
    public bool IsPublished { get; private set; }

    public IReadOnlyCollection<DailyMenuItem> Items => _items;

    public DailyMenuItem AddItem(Guid articleId, decimal effectivePrice, int? displayOrder = null)
    {
        Guard.NotEmpty(articleId, nameof(articleId));
        if (_items.Any(i => i.ArticleId == articleId))
        {
            throw new DomainException("duplicate_menu_item", "The article is already on this menu.");
        }

        Guard.NotNegative(effectivePrice, nameof(effectivePrice));
        Money.EnsureValid(effectivePrice, nameof(effectivePrice));
        var item = new DailyMenuItem(Id, articleId, effectivePrice, displayOrder ?? _items.Count);
        _items.Add(item);
        return item;
    }

    public void SetAvailability(Guid articleId, bool isAvailable)
    {
        var item = _items.FirstOrDefault(i => i.ArticleId == articleId)
                   ?? throw new DomainException("menu_item_not_found", "The article is not on this menu.");
        item.IsAvailable = isAvailable;
    }

    public void RemoveItem(Guid articleId)
    {
        if (_items.RemoveAll(i => i.ArticleId == articleId) == 0)
        {
            throw new DomainException("menu_item_not_found", "The article is not on this menu.");
        }
    }

    public void SetItemPrice(Guid articleId, decimal effectivePrice)
    {
        var item = _items.FirstOrDefault(i => i.ArticleId == articleId)
                   ?? throw new DomainException("menu_item_not_found", "The article is not on this menu.");
        Guard.NotNegative(effectivePrice, nameof(effectivePrice));
        item.EffectivePrice = Money.EnsureValid(effectivePrice, nameof(effectivePrice));
    }

    /// <summary>Takes the menu back to draft (e.g. to rework tomorrow's menu); registers stop receiving it.</summary>
    public void Unpublish() => IsPublished = false;

    public void Publish()
    {
        if (_items.Count == 0)
        {
            throw new DomainException("empty_menu", "An empty menu cannot be published.");
        }

        IsPublished = true;
    }

    /// <summary>Copies this menu (e.g. yesterday's or a template week) to another date/point of sale, as a draft.</summary>
    public DailyMenu CopyTo(DateOnly date, MealService? service = null, Guid? pointOfSaleId = null)
    {
        var copy = new DailyMenu(Guid.CreateVersion7(), pointOfSaleId ?? PointOfSaleId, date, service ?? Service);
        foreach (var item in _items.OrderBy(i => i.DisplayOrder))
        {
            copy.AddItem(item.ArticleId, item.EffectivePrice, item.DisplayOrder);
        }

        return copy;
    }
}

public sealed class DailyMenuItem : Entity
{
    private DailyMenuItem()
    {
    }

    internal DailyMenuItem(Guid dailyMenuId, Guid articleId, decimal effectivePrice, int displayOrder)
    {
        DailyMenuId = dailyMenuId;
        ArticleId = articleId;
        EffectivePrice = effectivePrice;
        DisplayOrder = displayOrder;
        IsAvailable = true;
    }

    public Guid DailyMenuId { get; private set; }
    public Guid ArticleId { get; private set; }

    /// <summary>Price frozen when the menu is built (resolved from price lists), TTC.</summary>
    public decimal EffectivePrice { get; internal set; }

    public bool IsAvailable { get; set; }
    public int DisplayOrder { get; set; }
}
