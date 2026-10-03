using Newrest.Pos.Application.Operations;

namespace Newrest.Pos.Api.Endpoints;

public static class SupervisionEndpoints
{
    public static RouteGroupBuilder MapSupervisionEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/supervision").WithTags("Supervision");
        g.MapGet("/dashboard", (SupervisionService s, CancellationToken ct) => s.GetDashboardAsync(ct))
            .WithSummary("Alerts and state of every register in the caller's scope.");
        g.MapGet("/reconciliation", (DateOnly from, DateOnly to, SupervisionService s, CancellationToken ct) => s.GetReconciliationAsync(from, to, ct))
            .WithSummary("Account debits without ticket, offline sales beyond the overdraft, tickets received late.");
        g.MapPost("/integrity-checks", (SupervisionService s, CancellationToken ct) => s.RunIntegrityChecksAsync(ct))
            .WithSummary("Verifies every register's ticket chain now (also run every night).");
        return api;
    }
}
