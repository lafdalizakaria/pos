namespace Newrest.Pos.Contracts.V1;

public sealed record DailyMenuItemDto(Guid ArticleId, string ArticleCode, string ArticleName, Guid CategoryId,
    decimal EffectivePrice, bool IsAvailable, int DisplayOrder);

public sealed record DailyMenuDto(Guid Id, Guid PointOfSaleId, DateOnly Date, string Service, bool IsPublished,
    IReadOnlyList<DailyMenuItemDto> Items);

/// <param name="ArticleIds">Initial items; prices are resolved from the price lists on <paramref name="Date"/>.</param>
public sealed record DailyMenuCreate(Guid PointOfSaleId, DateOnly Date, string Service, IReadOnlyList<Guid>? ArticleIds);

/// <param name="Price">Null = price resolved from the price lists.</param>
public sealed record MenuItemAdd(Guid ArticleId, decimal? Price);

public sealed record MenuItemUpdate(decimal Price, bool IsAvailable);

/// <summary>Copies one menu to other dates (e.g. yesterday → today).</summary>
public sealed record MenuCopyRequest(IReadOnlyList<DateOnly> TargetDates, Guid? TargetPointOfSaleId, bool Overwrite = false);

/// <summary>Copies every menu of a 7-day week (template week) onto another week.</summary>
public sealed record MenuWeekCopyRequest(Guid PointOfSaleId, DateOnly SourceWeekStart, DateOnly TargetWeekStart, bool Overwrite = false);

public sealed record MenuCopyResult(int Created, int Replaced, int Skipped, IReadOnlyList<Guid> MenuIds);
