using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Catalog;

public sealed class Category : ReferenceEntity
{
    private Category()
    {
    }

    public Category(Guid id, string code, string name, int displayOrder, string? colorHex = null) : base(id)
    {
        Code = Guard.NotBlank(code, nameof(code), 32).ToUpperInvariant();
        Name = Guard.NotBlank(name, nameof(name), 100);
        DisplayOrder = displayOrder;
        ColorHex = colorHex;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public string Name { get; set; } = null!;
    public int DisplayOrder { get; set; }
    public string? ColorHex { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>Master catalogue item. Prices are tax-inclusive (TTC) in MAD.</summary>
public sealed class Article : ReferenceEntity
{
    private readonly List<ArticlePhoto> _photos = [];

    private Article()
    {
    }

    public Article(Guid id, string code, string name, Guid categoryId, decimal basePrice, decimal vatRate) : base(id)
    {
        Code = Guard.NotBlank(code, nameof(code), 32).ToUpperInvariant();
        Name = Guard.NotBlank(name, nameof(name), 150);
        CategoryId = Guard.NotEmpty(categoryId, nameof(categoryId));
        SetBasePrice(basePrice);
        SetVatRate(vatRate);
        IsSubsidizable = true;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public string Name { get; set; } = null!;

    /// <summary>Short label printed on receipts (defaults to <see cref="Name"/>).</summary>
    public string? ReceiptLabel { get; set; }

    public Guid CategoryId { get; set; }
    public decimal BasePrice { get; private set; }

    /// <summary>VAT rate as a fraction (0.10 = 10 %).</summary>
    public decimal VatRate { get; private set; }

    /// <summary>Free-text visual description given to the vision model (colour, shape, garnish...).</summary>
    public string? VisualDescription { get; set; }

    /// <summary>Whether the employer subsidy applies to this article (e.g. false for bottled drinks if the contract says so).</summary>
    public bool IsSubsidizable { get; set; }

    public bool IsActive { get; set; }

    public IReadOnlyCollection<ArticlePhoto> Photos => _photos;

    public void SetBasePrice(decimal price)
    {
        Guard.NotNegative(price, nameof(price));
        BasePrice = Money.EnsureValid(price, nameof(price));
    }

    public void SetVatRate(decimal vatRate)
    {
        if (vatRate is < 0 or >= 1)
        {
            throw new DomainException("invalid_vat_rate", "VAT rate must be a fraction between 0 and 1.");
        }

        VatRate = vatRate;
    }

    public ArticlePhoto AddPhoto(string storagePath, string? caption = null)
    {
        var photo = new ArticlePhoto(Id, storagePath, caption, _photos.Count);
        _photos.Add(photo);
        return photo;
    }
}

public sealed class ArticlePhoto : Entity
{
    private ArticlePhoto()
    {
    }

    internal ArticlePhoto(Guid articleId, string storagePath, string? caption, int displayOrder)
    {
        ArticleId = articleId;
        StoragePath = Guard.NotBlank(storagePath, nameof(storagePath), 500);
        Caption = caption;
        DisplayOrder = displayOrder;
    }

    public Guid ArticleId { get; private set; }

    /// <summary>Relative path/key in the configured blob storage.</summary>
    public string StoragePath { get; private set; } = null!;

    public string? Caption { get; set; }
    public int DisplayOrder { get; set; }
}
