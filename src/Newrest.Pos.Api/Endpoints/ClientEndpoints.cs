using Newrest.Pos.Application.Clients;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.Api.Endpoints;

public static class ClientEndpoints
{
    public static RouteGroupBuilder MapClientEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup(string.Empty).WithTags("Clients");

        g.MapGet("/clients", (Guid? companyId, ClientService s, CancellationToken ct) => s.ListClientsAsync(companyId, ct));
        g.MapGet("/clients/{id:guid}", (Guid id, ClientService s, CancellationToken ct) => s.GetClientAsync(id, ct));
        g.MapPost("/clients", async (ClientCompanyUpsert r, ClientService s, CancellationToken ct) => Results.Created((string?)null, await s.CreateClientAsync(r, ct)));
        g.MapPut("/clients/{id:guid}", (Guid id, ClientCompanyUpsert r, ClientService s, CancellationToken ct) => s.UpdateClientAsync(id, r, ct));

        g.MapGet("/clients/{id:guid}/contracts", (Guid id, ClientService s, CancellationToken ct) => s.ListContractsAsync(id, ct));
        g.MapGet("/contracts/{id:guid}", (Guid id, ClientService s, CancellationToken ct) => s.GetContractAsync(id, ct));
        g.MapPost("/contracts", async (ContractUpsert r, ClientService s, CancellationToken ct) => Results.Created((string?)null, await s.CreateContractAsync(r, ct)));
        g.MapPut("/contracts/{id:guid}", (Guid id, ContractUpsert r, ClientService s, CancellationToken ct) => s.UpdateContractAsync(id, r, ct));
        g.MapPost("/contracts/{id:guid}/subsidy-rules", async (Guid id, SubsidyRuleCreate r, ClientService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.AddSubsidyRuleAsync(id, r, ct)));
        g.MapPost("/subsidy-rules/{id:guid}/close", (Guid id, SubsidyRuleClose r, ClientService s, CancellationToken ct) =>
            s.CloseSubsidyRuleAsync(id, r, ct)).WithSummary("Ends a rule; to change a rule, close it and create a new one.");

        g.MapGet("/clients/{id:guid}/diners", (Guid id, string? search, int? page, int? pageSize, DinerService s, CancellationToken ct) =>
            s.ListAsync(id, search, page ?? 1, pageSize ?? 50, ct));
        g.MapPost("/clients/{id:guid}/diners/import", async (Guid id, Guid contractId, bool? dryRun, IFormFile file, DinerService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return Results.Ok(await s.ImportAsync(id, contractId, file.FileName, stream, dryRun ?? false, ct));
            })
            .DisableAntiforgery()
            .WithSummary("Imports diners from CSV (UTF-8, ';' or ',') or Excel (.xlsx). dryRun=true validates without saving.");
        g.MapGet("/diners/{id:guid}", (Guid id, DinerService s, CancellationToken ct) => s.GetAsync(id, ct));
        g.MapGet("/diners/by-badge/{number}", (string number, DinerService s, CancellationToken ct) => s.FindByBadgeAsync(number, ct));
        g.MapPost("/diners", async (DinerCreate r, DinerService s, CancellationToken ct) => Results.Created((string?)null, await s.CreateAsync(r, ct)));
        g.MapPut("/diners/{id:guid}", (Guid id, DinerUpdate r, DinerService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));

        g.MapPost("/diners/{id:guid}/badges", async (Guid id, BadgeIssue r, DinerService s, CancellationToken ct) =>
            Results.Created((string?)null, await s.IssueBadgeAsync(id, r, ct)));
        g.MapPost("/badges/{id:guid}/lost", (Guid id, BadgeReplace r, DinerService s, CancellationToken ct) => s.ReplaceLostBadgeAsync(id, r, ct))
            .WithSummary("Declares the badge lost and issues its replacement; the balance stays on the account.");
        g.MapPost("/badges/{id:guid}/block", (Guid id, DinerService s, CancellationToken ct) => s.SetBadgeBlockedAsync(id, true, ct));
        g.MapPost("/badges/{id:guid}/unblock", (Guid id, DinerService s, CancellationToken ct) => s.SetBadgeBlockedAsync(id, false, ct));
        return api;
    }
}
