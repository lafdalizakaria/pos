using Newrest.Pos.Application.Catalog;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Endpoints;

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup(string.Empty).WithTags("Catalogue");

        g.MapGet("/categories", (CatalogService s, CancellationToken ct) => s.ListCategoriesAsync(ct));
        g.MapPost("/categories", async (CategoryUpsert r, CatalogService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateCategoryAsync(r, ct)));
        g.MapPut("/categories/{id:guid}", (Guid id, CategoryUpsert r, CatalogService s, CancellationToken ct) => s.UpdateCategoryAsync(id, r, ct));

        g.MapGet("/articles", (string? search, Guid? categoryId, bool? isActive, int? page, int? pageSize, CatalogService s, CancellationToken ct) =>
            s.ListArticlesAsync(search, categoryId, isActive, page ?? 1, pageSize ?? 50, ct));
        g.MapGet("/articles/{id:guid}", (Guid id, CatalogService s, CancellationToken ct) => s.GetArticleAsync(id, ct));
        g.MapPost("/articles", async (ArticleUpsert r, CatalogService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreateArticleAsync(r, ct)));
        g.MapPut("/articles/{id:guid}", (Guid id, ArticleUpsert r, CatalogService s, CancellationToken ct) => s.UpdateArticleAsync(id, r, ct));

        g.MapPost("/articles/{id:guid}/photos", async (Guid id, IFormFile file, string? caption, CatalogService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return Results.Created((string?)null, await s.AddPhotoAsync(id, file.FileName, stream, file.Length, caption, ct));
            })
            .DisableAntiforgery()
            .WithSummary("Uploads a reference photo (JPEG/PNG/WebP, max 5 MB) used by the vision service.");
        g.MapDelete("/articles/{id:guid}/photos/{photoId:guid}", async (Guid id, Guid photoId, CatalogService s, CancellationToken ct) =>
        {
            await s.DeletePhotoAsync(id, photoId, ct);
            return Results.NoContent();
        });
        g.MapGet("/photos/{photoId:guid}", async (Guid photoId, CatalogService s, CancellationToken ct) =>
            await s.OpenPhotoAsync(photoId, ct) is { } photo ? Results.Stream(photo.Content, photo.ContentType) : Results.NotFound());

        g.MapGet("/price-lists", (Guid? companyId, CatalogService s, CancellationToken ct) => s.ListPriceListsAsync(companyId, ct));
        g.MapGet("/price-lists/{id:guid}", (Guid id, CatalogService s, CancellationToken ct) => s.GetPriceListAsync(id, ct));
        g.MapPost("/price-lists", async (PriceListCreate r, CatalogService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.CreatePriceListAsync(r, ct)));
        g.MapPut("/price-lists/{id:guid}", (Guid id, PriceListUpdate r, CatalogService s, CancellationToken ct) => s.UpdatePriceListAsync(id, r, ct));
        g.MapPut("/price-lists/{id:guid}/prices/{articleId:guid}", (Guid id, Guid articleId, SetPriceRequest r, CatalogService s, CancellationToken ct) =>
            s.SetPriceAsync(id, articleId, r.Price, ct));
        g.MapDelete("/price-lists/{id:guid}/prices/{articleId:guid}", (Guid id, Guid articleId, CatalogService s, CancellationToken ct) =>
            s.RemovePriceAsync(id, articleId, ct));
        g.MapGet("/points-of-sale/{id:guid}/prices", (Guid id, DateOnly? date, CatalogService s, TimeProvider clock, CancellationToken ct) =>
            s.GetEffectivePricesAsync(id, date ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct));
        return api;
    }
}
