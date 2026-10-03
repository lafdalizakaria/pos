using Newrest.Pos.Application.Sales;

namespace Newrest.Pos.Api.Endpoints;

public static class SalesEndpoints
{
    public static RouteGroupBuilder MapSalesEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup(string.Empty).WithTags("Ventes");
        g.MapGet("/tickets", (Guid? pointOfSaleId, Guid? registerId, DateOnly? from, DateOnly? to, string? search, int? page, int? pageSize,
            TicketQueryService s, CancellationToken ct) => s.ListAsync(pointOfSaleId, registerId, from, to, search, page ?? 1, pageSize ?? 50, ct));
        g.MapGet("/tickets/{id:guid}", (Guid id, TicketQueryService s, CancellationToken ct) => s.GetAsync(id, ct));
        g.MapGet("/registers/{id:guid}/chain-verification", (Guid id, TicketQueryService s, CancellationToken ct) => s.VerifyChainAsync(id, ct))
            .WithSummary("Recomputes the SHA-256 chain of the register: gaps, broken links, altered tickets.");
        g.MapGet("/z-reports", (Guid? pointOfSaleId, Guid? registerId, DateOnly? from, DateOnly? to, TicketQueryService s, CancellationToken ct) =>
            s.ListZReportsAsync(pointOfSaleId, registerId, from, to, ct));
        return api;
    }
}
