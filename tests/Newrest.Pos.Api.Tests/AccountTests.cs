using System.Net;
using System.Net.Http.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class AccountTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    private static readonly Guid Youssef = DemoDataSeeder.Id("account:ATL0001"); // prepaid, 300 MAD
    private static readonly Guid Mehdi = DemoDataSeeder.Id("account:ATL0003");   // prepaid, 20 MAD
    private static readonly Guid Amine = DemoDataSeeder.Id("account:SAH0001");   // NMS

    [Fact]
    public async Task Top_up_is_idempotent_and_audited()
    {
        var request = new TopUpRequest(100m, "Cash", Guid.NewGuid(), "Recharge guichet");
        var first = await ReadAsync<LedgerResultDto>(await ManagerClient().PostAsJsonAsync($"/api/v1/accounts/{Youssef}/top-ups", request));
        var replay = await ReadAsync<LedgerResultDto>(await ManagerClient().PostAsJsonAsync($"/api/v1/accounts/{Youssef}/top-ups", request));

        first.BalanceAfter.Should().Be(400m);
        replay.WasDuplicate.Should().BeTrue();
        replay.MovementId.Should().Be(first.MovementId);
        (await ReadAsync<AccountDto>(await ViewerClient().GetAsync($"/api/v1/accounts/{Youssef}"))).Balance.Should().Be(400m);

        var conflict = await ManagerClient().PostAsJsonAsync($"/api/v1/accounts/{Youssef}/top-ups", request with { Amount = 101m });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(conflict)).Should().Be("idempotency_conflict");
        (await ManagerClient().PostAsJsonAsync($"/api/v1/accounts/{Youssef}/top-ups", new TopUpRequest(-5m, "Cash", Guid.NewGuid(), null)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ManagerClient().PostAsJsonAsync($"/api/v1/accounts/{Youssef}/top-ups", new TopUpRequest(5m, "Account", Guid.NewGuid(), null)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ViewerClient().PostAsJsonAsync($"/api/v1/accounts/{Youssef}/top-ups", new TopUpRequest(5m, "Cash", Guid.NewGuid(), null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ManagerClient().PostAsJsonAsync($"/api/v1/accounts/{Amine}/top-ups", new TopUpRequest(5m, "Cash", Guid.NewGuid(), null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "NMS is outside the manager's scope");

        var audit = await ReadAsync<PagedResult<AuditEntryDto>>(await AccountantClient().GetAsync($"/api/v1/audit?entityType=Account&entityId={Youssef}"));
        audit.Items.Should().ContainSingle(a => a.Action == "AccountTopUp" && a.Actor == CasaManager);
    }

    [Fact]
    public async Task Finance_corrects_reverses_and_refunds_within_limits()
    {
        var finance = AccountantClient();
        (await ManagerClient().PostAsJsonAsync($"/api/v1/accounts/{Mehdi}/corrections", new CorrectionRequest(-5m, "x", Guid.NewGuid())))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "corrections are reserved to finance");

        var overdraw = await finance.PostAsJsonAsync($"/api/v1/accounts/{Mehdi}/corrections", new CorrectionRequest(-25m, "Erreur", Guid.NewGuid()));
        overdraw.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(overdraw)).Should().Be("insufficient_funds");
        (await finance.PostAsJsonAsync($"/api/v1/accounts/{Mehdi}/corrections", new CorrectionRequest(-5m, " ", Guid.NewGuid())))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a reason is mandatory");

        var correction = await ReadAsync<LedgerResultDto>(await finance.PostAsJsonAsync($"/api/v1/accounts/{Mehdi}/corrections",
            new CorrectionRequest(-5m, "Double recharge", Guid.NewGuid())));
        correction.BalanceAfter.Should().Be(15m);

        var reversal = await ReadAsync<LedgerResultDto>(await finance.PostAsJsonAsync(
            $"/api/v1/accounts/{Mehdi}/movements/{correction.MovementId}/reverse", new ReversalRequest("Annulation", Guid.NewGuid())));
        reversal.BalanceAfter.Should().Be(20m);
        (await finance.PostAsJsonAsync($"/api/v1/accounts/{Mehdi}/movements/{correction.MovementId}/reverse", new ReversalRequest("bis", Guid.NewGuid())))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await finance.PostAsJsonAsync($"/api/v1/accounts/{Mehdi}/refunds", new RefundRequest(25m, "Cash", "Départ", Guid.NewGuid())))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var refund = await ReadAsync<LedgerResultDto>(await finance.PostAsJsonAsync($"/api/v1/accounts/{Mehdi}/refunds",
            new RefundRequest(20m, "Cash", "Départ de l'entreprise", Guid.NewGuid())));
        refund.Amount.Should().Be(-20m);
        refund.BalanceAfter.Should().Be(0m);

        var movements = await ReadAsync<PagedResult<MovementDto>>(await finance.GetAsync($"/api/v1/accounts/{Mehdi}/movements"));
        movements.TotalCount.Should().Be(4);
        movements.Items[0].Type.Should().Be("Refund");
        movements.Items.Single(m => m.Id == correction.MovementId).IsReversed.Should().BeTrue();
        movements.Items.Single(m => m.Id == reversal.MovementId).ReversesMovementId.Should().Be(correction.MovementId);
        movements.Items.Should().OnlyContain(m => m.PerformedBy == "seed" || m.PerformedBy == NfmsAccountant);

        var audit = await ReadAsync<PagedResult<AuditEntryDto>>(await finance.GetAsync($"/api/v1/audit?entityType=Account&entityId={Mehdi}"));
        audit.Items.Select(a => a.Action).Should().BeEquivalentTo(["AccountCorrection", "AccountReversal", "AccountRefund"]);
    }

    [Fact]
    public async Task Account_settings_and_listing()
    {
        var updated = await ReadAsync<AccountDto>(await ManagerClient().PutAsJsonAsync($"/api/v1/accounts/{Mehdi}", new AccountUpdate("Mixed", 50m, true)));
        updated.AvailableToSpend.Should().Be(70m);
        (await ManagerClient().PutAsJsonAsync($"/api/v1/accounts/{Mehdi}", new AccountUpdate("Mixed", -1m, true)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var list = await ReadAsync<PagedResult<AccountDto>>(await ViewerClient().GetAsync($"/api/v1/clients/{Atlas}/accounts?search=Chraibi"));
        list.Items.Should().ContainSingle().Which.Should().Match<AccountDto>(a => a.Type == "Mixed" && a.EmployeeNumber == "MATL0003");
        (await ViewerClient().GetAsync($"/api/v1/accounts/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var outOfScope = await ViewerClient().GetAsync($"/api/v1/accounts/{Amine}");
        outOfScope.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
