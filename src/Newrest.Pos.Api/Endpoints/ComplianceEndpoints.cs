using Newrest.Pos.Application.Compliance;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Endpoints;

public static class ComplianceEndpoints
{
    public static RouteGroupBuilder MapComplianceEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/compliance").WithTags("Conformité");
        g.MapGet("/archives", (ArchiveService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("/archives", async (ArchiveCreate r, ArchiveService s, CancellationToken ct) => Results.Created((string?)null, await s.CreateAsync(r, ct)))
            .WithSummary("Seals a month: signed archive of tickets, Z reports and ledger (administrators, finance).");
        g.MapGet("/archives/{id:guid}/file", async (Guid id, ArchiveService s, CancellationToken ct) =>
            await s.OpenAsync(id, ct) is { } file ? Results.File(file.Content, "application/zip", file.FileName) : Results.NotFound());
        g.MapPost("/archives/{id:guid}/verify", (Guid id, ArchiveService s, CancellationToken ct) => s.VerifyAsync(id, ct))
            .WithSummary("Re-verifies a stored archive: file hash, signature, every ticket hash, chains, continuity.");
        g.MapPost("/anonymization", (AnonymizationRequest r, PrivacyService s, CancellationToken ct) => s.AnonymizeInactiveDinersAsync(r, ct))
            .WithSummary("Anonymises diners who left (inactive, zero balance, no movement since the cut-off). DryRun to count first.");
        return api;
    }
}
