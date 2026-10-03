using System.Security.Claims;
using Newrest.Pos.Api.Security;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Catalog;
using Newrest.Pos.Application.Operations;
using Newrest.Pos.Application.Sync;
using Newrest.Pos.Application.Vision;
using Newrest.Pos.Contracts.V1.Sync;

namespace Newrest.Pos.Api.Endpoints;

/// <summary>Endpoints called by registers (register token only). The register identity always comes from the token.</summary>
public static class RegisterSyncEndpoints
{
    public static RouteGroupBuilder MapRegisterSyncEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/register").WithTags("Caisses (synchronisation)").RequireAuthorization(AuthenticationSetup.RegisterPolicy);

        g.MapGet("/profile", (ClaimsPrincipal user, RegisterReferenceService s, CancellationToken ct) => s.GetProfileAsync(RegisterId(user), ct));
        g.MapGet("/reference", (string? cursor, ClaimsPrincipal user, RegisterReferenceService s, CancellationToken ct) =>
                s.GetReferenceAsync(RegisterId(user), cursor, ct))
            .WithSummary("Reference data for the offline cache; pass back the cursor to receive only changes.");
        g.MapGet("/badges/{number}", (string number, DateOnly businessDate, ClaimsPrincipal user, RegisterReferenceService s, CancellationToken ct) =>
                s.GetBadgeContextAsync(RegisterId(user), number, businessDate, ct))
            .WithSummary("Online badge lookup: balance and subsidy already granted today on every point of sale.");

        g.MapPost("/cash-sessions", (CashSessionSyncDto dto, ClaimsPrincipal user, RegisterSyncService s, CancellationToken ct) =>
            s.OpenCashSessionAsync(RegisterId(user), dto, ct));
        g.MapPost("/account-movements", (AccountMovementSyncDto dto, ClaimsPrincipal user, RegisterSyncService s, CancellationToken ct) =>
                s.PostAccountMovementAsync(RegisterId(user), dto, ct))
            .WithSummary("Online debit (balance enforced) or offline replay (recorded and flagged). Idempotent.");
        g.MapPost("/tickets", (TicketSyncDto dto, ClaimsPrincipal user, RegisterSyncService s, CancellationToken ct) =>
                s.IngestTicketAsync(RegisterId(user), dto, ct))
            .WithSummary("Fiscal ticket, in sequence order. Idempotent; the server checks hash and chain.");
        g.MapPost("/z-reports", (ZReportSyncDto dto, ClaimsPrincipal user, RegisterSyncService s, CancellationToken ct) =>
            s.IngestZReportAsync(RegisterId(user), dto, ct));
        g.MapPost("/recognitions", (RecognitionSyncDto dto, ClaimsPrincipal user, RecognitionService s, CancellationToken ct) =>
                s.IngestAsync(RegisterId(user), dto, ct))
            .WithSummary("Tray recognition outcome (vision KPIs). Idempotent.");
        g.MapGet("/photos/{photoId:guid}", async (Guid photoId, CatalogService s, CancellationToken ct) =>
                await s.OpenPhotoForRegisterAsync(photoId, ct) is { } photo ? Results.Stream(photo.Content, photo.ContentType) : Results.NotFound())
            .WithSummary("Reference photo of an article, sent by the register to its vision service.");
        g.MapPost("/heartbeat", async (RegisterHeartbeatDto dto, ClaimsPrincipal user, SupervisionService s, CancellationToken ct) =>
            {
                await s.RecordHeartbeatAsync(RegisterId(user), dto, ct);
                return Results.NoContent();
            })
            .WithSummary("Register state every minute: version, queue, blocking error, open session, last local backup.");
        g.MapGet("/vision", (ClaimsPrincipal user, VisionModelService s, CancellationToken ct) => s.GetRegisterConfigAsync(RegisterId(user), ct))
            .WithSummary("Vision settings of the register's site (provider, thresholds, model to install).");
        g.MapGet("/vision-models/{id:guid}/file", async (Guid id, ClaimsPrincipal user, VisionModelService s, CancellationToken ct) =>
                await s.OpenModelForRegisterAsync(RegisterId(user), id, ct) is { } stream
                    ? Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true)
                    : Results.NotFound())
            .WithSummary("ONNX file of a published model (the register checks its SHA-256 before installing it).");
        g.MapPost("/vision/status", async (RegisterVisionReportDto dto, ClaimsPrincipal user, VisionModelService s, CancellationToken ct) =>
        {
            await s.ReportRegisterStatusAsync(RegisterId(user), dto, ct);
            return Results.NoContent();
        });
        return api;
    }

    private static Guid RegisterId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(PosClaims.RegisterId)?.Value, out var id) ? id : throw new ForbiddenException("Register token required.");
}
