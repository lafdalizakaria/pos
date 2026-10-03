using Newrest.Pos.Application.Menus;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Endpoints;

public static class MenuEndpoints
{
    public static RouteGroupBuilder MapMenuEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/daily-menus").WithTags("Menus");

        g.MapGet("/", (Guid pointOfSaleId, DateOnly from, DateOnly? to, MenuService s, CancellationToken ct) =>
            s.ListAsync(pointOfSaleId, from, to ?? from, ct));
        g.MapGet("/{id:guid}", (Guid id, MenuService s, CancellationToken ct) => s.GetAsync(id, ct));
        g.MapPost("/", async (DailyMenuCreate r, MenuService s, CancellationToken ct) => Results.Created((string?)null, await s.CreateAsync(r, ct)));
        g.MapDelete("/{id:guid}", async (Guid id, MenuService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        g.MapPost("/{id:guid}/items", (Guid id, MenuItemAdd r, MenuService s, CancellationToken ct) => s.AddItemAsync(id, r, ct));
        g.MapPut("/{id:guid}/items/{articleId:guid}", (Guid id, Guid articleId, MenuItemUpdate r, MenuService s, CancellationToken ct) =>
            s.UpdateItemAsync(id, articleId, r, ct));
        g.MapDelete("/{id:guid}/items/{articleId:guid}", (Guid id, Guid articleId, MenuService s, CancellationToken ct) =>
            s.RemoveItemAsync(id, articleId, ct));
        g.MapPost("/{id:guid}/publish", (Guid id, MenuService s, CancellationToken ct) => s.SetPublishedAsync(id, true, ct));
        g.MapPost("/{id:guid}/unpublish", (Guid id, MenuService s, CancellationToken ct) => s.SetPublishedAsync(id, false, ct));
        g.MapPost("/{id:guid}/copy", (Guid id, MenuCopyRequest r, MenuService s, CancellationToken ct) => s.CopyAsync(id, r, ct))
            .WithSummary("Copies a menu to other dates (e.g. yesterday → today).");
        g.MapPost("/copy-week", (MenuWeekCopyRequest r, MenuService s, CancellationToken ct) => s.CopyWeekAsync(r, ct))
            .WithSummary("Copies a template week onto another week.");
        return api;
    }
}
