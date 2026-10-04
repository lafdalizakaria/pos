namespace Newrest.Pos.Contracts.V1;

public sealed record CategoryDto(Guid Id, string Code, string Name, int DisplayOrder, string? ColorHex, bool IsActive);

public sealed record CategoryUpsert(string Code, string Name, int DisplayOrder, string? ColorHex, bool IsActive = true);

public sealed record ArticlePhotoDto(Guid Id, string StoragePath, string? Caption, int DisplayOrder);

public sealed record ArticleDto(Guid Id, string Code, string Name, string? ReceiptLabel, Guid CategoryId, decimal BasePrice,
    decimal VatRate, string? VisualDescription, bool IsSubsidizable, bool IsActive, IReadOnlyList<ArticlePhotoDto> Photos);

public sealed record ArticleUpsert(string Code, string Name, string? ReceiptLabel, Guid CategoryId, decimal BasePrice,
    decimal VatRate, string? VisualDescription, bool IsSubsidizable = true, bool IsActive = true);

public sealed record PriceOverrideDto(Guid ArticleId, string ArticleCode, string ArticleName, decimal Price);

public sealed record PriceListDto(Guid Id, string Code, string Name, Guid CompanyId, Guid? SiteId, Guid? PointOfSaleId,
    DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive, IReadOnlyList<PriceOverrideDto> Overrides);

public sealed record PriceListCreate(string Code, string Name, Guid CompanyId, Guid? SiteId, Guid? PointOfSaleId,
    DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record PriceListUpdate(string Name, DateOnly ValidFrom, DateOnly? ValidTo, bool IsActive);

public sealed record SetPriceRequest(decimal Price);

public sealed record EffectivePriceDto(Guid ArticleId, string ArticleCode, string ArticleName, decimal BasePrice, decimal EffectivePrice);
