using Microsoft.AspNetCore.Mvc;
using Newrest.Pos.Application.Vision;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Endpoints;

/// <summary>Vision model deployment: models trained centrally, settings per site, state reported by the registers.</summary>
public static class VisionEndpoints
{
    public static RouteGroupBuilder MapVisionEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/vision").WithTags("Vision");
        g.MapGet("/models", (VisionModelService s, CancellationToken ct) => s.ListModelsAsync(ct));
        g.MapPost("/models", async (IFormFile model, IFormFile manifest, VisionModelService s, CancellationToken ct) =>
            {
                using var reader = new StreamReader(manifest.OpenReadStream());
                var json = await reader.ReadToEndAsync(ct);
                await using var stream = model.OpenReadStream();
                return Results.Created((string?)null, await s.UploadAsync(stream, json, ct));
            })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(VisionModelService.MaxModelBytes + 1024 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: VisionModelService.MaxModelBytes + 1024 * 1024)
            .WithSummary("Imports a trained model: model.onnx + manifest.json from `python -m training train` (administrators).");
        g.MapPost("/models/{id:guid}/publish", (Guid id, VisionModelService s, CancellationToken ct) => s.PublishAsync(id, ct));
        g.MapPost("/models/{id:guid}/retire", (Guid id, VisionModelService s, CancellationToken ct) => s.RetireAsync(id, ct));
        g.MapGet("/sites", (VisionModelService s, CancellationToken ct) => s.ListSiteSettingsAsync(ct));
        g.MapPut("/sites/{siteId:guid}", (Guid siteId, SiteVisionSettingsUpdate r, VisionModelService s, CancellationToken ct) =>
                s.UpdateSiteSettingsAsync(siteId, r, ct))
            .WithSummary("Provider (Mock, Gemini, Yolo, Hybrid), model and thresholds of a site; applied by its registers at their next sync.");
        g.MapGet("/registers", (VisionModelService s, CancellationToken ct) => s.ListRegisterStatusAsync(ct));
        return api;
    }
}
