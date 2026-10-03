using Newrest.Pos.Application.Accounts;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Endpoints;

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup(string.Empty).WithTags("Comptes");

        g.MapGet("/clients/{id:guid}/accounts", (Guid id, string? search, int? page, int? pageSize, AccountService s, CancellationToken ct) =>
            s.ListAsync(id, search, page ?? 1, pageSize ?? 50, ct));
        g.MapGet("/accounts/{id:guid}", (Guid id, AccountService s, CancellationToken ct) => s.GetAsync(id, ct));
        g.MapPut("/accounts/{id:guid}", (Guid id, AccountUpdate r, AccountService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        g.MapGet("/accounts/{id:guid}/movements", (Guid id, DateOnly? from, DateOnly? to, int? page, int? pageSize, AccountService s, CancellationToken ct) =>
            s.ListMovementsAsync(id, from, to, page ?? 1, pageSize ?? 50, ct));
        g.MapPost("/accounts/{id:guid}/top-ups", (Guid id, TopUpRequest r, AccountService s, CancellationToken ct) => s.TopUpAsync(id, r, ct));
        g.MapPost("/accounts/{id:guid}/corrections", (Guid id, CorrectionRequest r, AccountService s, CancellationToken ct) => s.CorrectAsync(id, r, ct));
        g.MapPost("/accounts/{id:guid}/movements/{movementId:guid}/reverse", (Guid id, Guid movementId, ReversalRequest r, AccountService s,
            CancellationToken ct) => s.ReverseAsync(id, movementId, r, ct));
        g.MapPost("/accounts/{id:guid}/refunds", (Guid id, RefundRequest r, AccountService s, CancellationToken ct) => s.RefundAsync(id, r, ct));

        api.MapGet("/audit", (string? entityType, string? entityId, string? actor, string? action, DateTimeOffset? from, DateTimeOffset? to,
                int? page, int? pageSize, AuditQueryService s, CancellationToken ct) =>
                s.SearchAsync(entityType, entityId, actor, action, from, to, page ?? 1, pageSize ?? 50, ct))
            .WithTags("Audit");
        return api;
    }
}
