using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Sales;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class RegisterSyncTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    private static readonly Guid Cas1 = DemoDataSeeder.Id("register:CAS1");
    private static readonly Guid Cashier = DemoDataSeeder.Id("operator:NFMS:CAIS01");
    private static readonly Guid Khadija = DemoDataSeeder.Id("account:ATL0002"); // prepaid 150 MAD
    private static readonly Guid KhadijaDiner = DemoDataSeeder.Id("diner:ATL0002");
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<HttpClient> RegisterClientAsync(Guid registerId)
    {
        var key = await ReadAsync<DeviceKeyIssued>(await AdminClient().PostAsync($"/api/v1/registers/{registerId}/device-key", null));
        var token = await ReadAsync<RegisterTokenResponse>(await Anonymous().PostAsJsonAsync("/api/v1/auth/register-token",
            new RegisterTokenRequest(registerId, key.DeviceKey)));
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static Ticket Sale(long sequence, string previous, Guid session, params PaymentInput[] payments) => Ticket.Issue(new TicketIssueRequest
    {
        Id = Guid.NewGuid(),
        RegisterId = Cas1,
        RegisterPrefix = "CAS1",
        Sequence = sequence,
        PreviousHash = previous,
        CashSessionId = session,
        OperatorId = Cashier,
        BusinessDate = Today,
        IssuedAt = DateTimeOffset.UtcNow,
        Lines = [new TicketLineInput(CouscousPoulet, "CSC-PLT", "Couscous poulet", 1, 38m, 0.10m)],
        Payments = payments.Length > 0 ? payments : [new PaymentInput(PaymentMethod.Cash, 38m) { Tendered = 50m }],
    });

    private static async Task<Guid> OpenSessionAsync(HttpClient register)
    {
        var session = new CashSessionSyncDto(Guid.NewGuid(), Cashier, DateTimeOffset.UtcNow.AddMinutes(-30), Today, 200m);
        (await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/cash-sessions", session))).WasDuplicate.Should().BeFalse();
        return session.Id;
    }

    [Fact]
    public async Task Tickets_are_ingested_in_order_idempotently_and_verified()
    {
        var register = await RegisterClientAsync(Cas1);
        var profile = await ReadAsync<RegisterProfileDto>(await register.GetAsync("/api/v1/register/profile"));
        profile.TicketPrefix.Should().Be("CAS1");
        profile.LastSyncedTicketSequence.Should().Be(0);
        profile.LegalName.Should().Contain("Newrest");

        var session = await OpenSessionAsync(register);
        var t1 = Sale(1, TicketHasher.GenesisHash, session);
        var t2 = Sale(2, t1.Hash, session);
        var t3 = Sale(3, t2.Hash, session);

        var gap = await register.PostAsJsonAsync("/api/v1/register/tickets", t2.ToSyncDto());
        gap.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(gap)).Should().Be("sequence_gap");

        (await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/tickets", t1.ToSyncDto()))).WasDuplicate.Should().BeFalse();
        (await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/tickets", t1.ToSyncDto()))).WasDuplicate.Should().BeTrue();

        var tampered = t2.ToSyncDto() with { Lines = [t2.ToSyncDto().Lines[0] with { Label = "Couscous royal" }] };
        var mismatch = await register.PostAsJsonAsync("/api/v1/register/tickets", tampered);
        (await ProblemCodeAsync(mismatch)).Should().Be("hash_mismatch");

        var forgedChain = Sale(2, new string('a', 64), session);
        (await ProblemCodeAsync(await register.PostAsJsonAsync("/api/v1/register/tickets", forgedChain.ToSyncDto()))).Should().Be("chain_broken");

        await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/tickets", t2.ToSyncDto()));
        await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/tickets", t3.ToSyncDto()));
        var reused = Sale(3, t2.Hash, session);
        (await register.PostAsJsonAsync("/api/v1/register/tickets", reused.ToSyncDto())).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await ReadAsync<RegisterProfileDto>(await register.GetAsync("/api/v1/register/profile"))).Should()
            .Match<RegisterProfileDto>(p => p.LastSyncedTicketSequence == 3 && p.LastSyncedTicketHash == t3.Hash);
        (await AdminClient().GetAsync("/api/v1/register/profile")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "back-office users are not registers");
    }

    [Fact]
    public async Task Account_payment_needs_its_movement_and_online_debits_enforce_the_balance()
    {
        var register = await RegisterClientAsync(Cas1);
        var session = await OpenSessionAsync(register);
        var movementKey = Guid.NewGuid();
        var ticket = Ticket.Issue(new TicketIssueRequest
        {
            Id = Guid.NewGuid(),
            RegisterId = Cas1,
            RegisterPrefix = "CAS1",
            Sequence = 1,
            PreviousHash = TicketHasher.GenesisHash,
            CashSessionId = session,
            OperatorId = Cashier,
            BusinessDate = Today,
            IssuedAt = DateTimeOffset.UtcNow,
            Lines = [new TicketLineInput(CouscousViande, "CSC-VND", "Couscous viande", 1, 42m, 0.10m)],
            DinerId = KhadijaDiner,
            AccountId = Khadija,
            SubsidyAmount = 20m,
            Payments = [new PaymentInput(PaymentMethod.Account, 22m) { AccountMovementId = movementKey }],
        });

        (await ProblemCodeAsync(await register.PostAsJsonAsync("/api/v1/register/tickets", ticket.ToSyncDto()))).Should().Be("movement_missing");

        var debit = new AccountMovementSyncDto(movementKey, Khadija, "Consumption", -22m, DateTimeOffset.UtcNow, null, ticket.Id, Cashier, null, false);
        (await ReadAsync<LedgerResultDto>(await register.PostAsJsonAsync("/api/v1/register/account-movements", debit))).BalanceAfter.Should().Be(128m);
        (await ReadAsync<LedgerResultDto>(await register.PostAsJsonAsync("/api/v1/register/account-movements", debit))).WasDuplicate.Should().BeTrue();
        await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/tickets", ticket.ToSyncDto()));

        var context = await ReadAsync<BadgeContextDto>(await register.GetAsync($"/api/v1/register/badges/bdg-atl0002?businessDate={Today:yyyy-MM-dd}"));
        context.Account!.Balance.Should().Be(128m);
        context.SubsidyGrantedToday.Should().Be(20m);
        context.SubsidizedMealsToday.Should().Be(1);
        context.SubsidyRules.Should().ContainSingle(r => r.DinerCategory == null, "the generic Atlas rule applies");

        var tooMuch = new AccountMovementSyncDto(Guid.NewGuid(), Khadija, "Consumption", -500m, DateTimeOffset.UtcNow, null, null, Cashier, null, false);
        (await ProblemCodeAsync(await register.PostAsJsonAsync("/api/v1/register/account-movements", tooMuch))).Should().Be("insufficient_funds");
        var offline = await ReadAsync<LedgerResultDto>(await register.PostAsJsonAsync("/api/v1/register/account-movements", tooMuch with { IsOfflineReplay = true }));
        offline.BalanceAfter.Should().Be(-372m);

        var sahara = new AccountMovementSyncDto(Guid.NewGuid(), DemoDataSeeder.Id("account:SAH0002"), "Consumption", -1m, DateTimeOffset.UtcNow, null, null, Cashier, null, false);
        (await ProblemCodeAsync(await register.PostAsJsonAsync("/api/v1/register/account-movements", sahara))).Should().Be("account_not_accepted");
        var correction = debit with { IdempotencyKey = Guid.NewGuid(), Type = "Correction" };
        (await register.PostAsJsonAsync("/api/v1/register/account-movements", correction)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Credit_note_with_account_refund_and_Z_report_close_the_session()
    {
        var register = await RegisterClientAsync(Cas1);
        var session = await OpenSessionAsync(register);
        (await ProblemCodeAsync(await register.PostAsJsonAsync("/api/v1/register/cash-sessions",
            new CashSessionSyncDto(Guid.NewGuid(), Cashier, DateTimeOffset.UtcNow, Today, 0m)))).Should().Be("session_already_open");

        var debitKey = Guid.NewGuid();
        var sale = Ticket.Issue(new TicketIssueRequest
        {
            Id = Guid.NewGuid(),
            RegisterId = Cas1,
            RegisterPrefix = "CAS1",
            Sequence = 1,
            PreviousHash = TicketHasher.GenesisHash,
            CashSessionId = session,
            OperatorId = Cashier,
            BusinessDate = Today,
            IssuedAt = DateTimeOffset.UtcNow,
            Lines = [new TicketLineInput(CouscousPoulet, "CSC-PLT", "Couscous poulet", 1, 38m, 0.10m)],
            DinerId = KhadijaDiner,
            AccountId = Khadija,
            Payments = [new PaymentInput(PaymentMethod.Account, 38m) { AccountMovementId = debitKey }],
        });
        await ReadAsync<LedgerResultDto>(await register.PostAsJsonAsync("/api/v1/register/account-movements",
            new AccountMovementSyncDto(debitKey, Khadija, "Consumption", -38m, DateTimeOffset.UtcNow, null, sale.Id, Cashier, null, false)));
        await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/tickets", sale.ToSyncDto()));

        var refundKey = Guid.NewGuid();
        var credit = Ticket.IssueCreditNote(sale, new CreditNoteRequest
        {
            Id = Guid.NewGuid(),
            RegisterPrefix = "CAS1",
            Sequence = 2,
            PreviousHash = sale.Hash,
            CashSessionId = session,
            OperatorId = Cashier,
            BusinessDate = Today,
            IssuedAt = DateTimeOffset.UtcNow,
            Reason = "Plat refusé",
            RefundPayments = [new PaymentInput(PaymentMethod.Account, -38m) { AccountMovementId = refundKey }],
        });
        (await ReadAsync<LedgerResultDto>(await register.PostAsJsonAsync("/api/v1/register/account-movements",
            new AccountMovementSyncDto(refundKey, Khadija, "Refund", 38m, DateTimeOffset.UtcNow, null, credit.Id, Cashier, "Avoir", false)))).BalanceAfter.Should().Be(150m);
        await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/tickets", credit.ToSyncDto()));

        var topUpKey = Guid.NewGuid();
        await ReadAsync<LedgerResultDto>(await register.PostAsJsonAsync("/api/v1/register/account-movements",
            new AccountMovementSyncDto(topUpKey, Khadija, "TopUp", 100m, DateTimeOffset.UtcNow, "Cash", null, Cashier, null, false)));

        var z = new ZReportSyncDto(Guid.NewGuid(), session, 1, DateTimeOffset.UtcNow.AddSeconds(5), 300m, Cashier, false, 2, 0m, 0m, 300m);
        (await ProblemCodeAsync(await register.PostAsJsonAsync("/api/v1/register/z-reports", z with { ExpectedCash = 299m }))).Should().Be("z_mismatch");
        (await ProblemCodeAsync(await register.PostAsJsonAsync("/api/v1/register/z-reports", z with { LastTicketSequence = 5 }))).Should().Be("tickets_missing");
        (await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/z-reports", z))).WasDuplicate.Should().BeFalse();
        (await ReadAsync<SyncAck>(await register.PostAsJsonAsync("/api/v1/register/z-reports", z))).WasDuplicate.Should().BeTrue();
        (await ReadAsync<RegisterProfileDto>(await register.GetAsync("/api/v1/register/profile"))).LastZNumber.Should().Be(1);
        await OpenSessionAsync(register);
    }

    [Fact]
    public async Task Reference_sync_is_incremental_and_scoped()
    {
        var register = await RegisterClientAsync(Cas1);
        var full = await ReadAsync<ReferenceSyncResponse>(await register.GetAsync("/api/v1/register/reference"));
        full.IsFull.Should().BeTrue();
        full.Articles.Should().HaveCount(30);
        full.Operators.Select(o => o.Code).Should().BeEquivalentTo(["CAIS01", "RESP01"], "CAIS02 is restricted to Tanger");
        full.Badges.Should().HaveCount(5, "only Atlas diners can eat at Casablanca");
        full.Accounts.Should().OnlyContain(a => a.ContractId == AtlasContract);
        full.Contracts.Should().ContainSingle(c => c.AcceptsThisPointOfSale);
        full.SubsidyRules.Should().HaveCount(2);
        full.Menus.Should().ContainSingle().Which.Items.Should().Contain(i => i.ArticleCode == "CSC-VND");
        full.Operators.Should().OnlyContain(o => o.PinHash.StartsWith("PBKDF2-SHA256$"));

        var empty = await ReadAsync<ReferenceSyncResponse>(await register.GetAsync($"/api/v1/register/reference?cursor={Uri.EscapeDataString(full.Cursor)}"));
        empty.IsFull.Should().BeFalse();
        (empty.Articles.Count + empty.Badges.Count + empty.Accounts.Count + empty.Diners.Count + empty.Operators.Count).Should().Be(0);
        empty.Menus.Should().ContainSingle("menus are always sent in full");

        var badge = (await ReadAsync<DinerDto>(await AdminClient().GetAsync("/api/v1/diners/by-badge/BDG-ATL0003"))).Badges.Single();
        await ReadAsync<BadgeDto>(await AdminClient().PostAsJsonAsync($"/api/v1/badges/{badge.Id}/lost", new BadgeReplace("BDG-ATL0003-B")));
        await ReadAsync<LedgerResultDto>(await AdminClient().PostAsJsonAsync($"/api/v1/accounts/{DemoDataSeeder.Id("account:ATL0001")}/top-ups",
            new TopUpRequest(10m, "Cash", Guid.NewGuid(), null)));
        var delta = await ReadAsync<ReferenceSyncResponse>(await register.GetAsync($"/api/v1/register/reference?cursor={Uri.EscapeDataString(empty.Cursor)}"));
        delta.IsFull.Should().BeFalse();
        delta.Badges.Select(b => (b.Number, b.Status)).Should().BeEquivalentTo([("BDG-ATL0003", "Lost"), ("BDG-ATL0003-B", "Active")]);
        delta.Accounts.Should().ContainSingle().Which.Balance.Should().Be(310m);
        delta.Articles.Should().BeEmpty();

        var contract = await ReadAsync<ContractDto>(await AdminClient().GetAsync($"/api/v1/contracts/{AtlasContract}"));
        await ReadAsync<ContractDto>(await AdminClient().PutAsJsonAsync($"/api/v1/contracts/{AtlasContract}", new ContractUpsert(contract.ClientCompanyId,
            contract.Reference, contract.StartDate, contract.EndDate, contract.BillingMode, [.. contract.PointOfSaleIds.Where(p => p != CasaSelf)])));
        var afterContract = await ReadAsync<ReferenceSyncResponse>(await register.GetAsync($"/api/v1/register/reference?cursor={Uri.EscapeDataString(delta.Cursor)}"));
        afterContract.IsFull.Should().BeTrue("contract scope changed");
        afterContract.Badges.Should().BeEmpty("Atlas no longer eats at Casablanca self");

        (await register.GetAsync("/api/v1/register/reference?cursor=garbage")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await register.GetAsync($"/api/v1/register/badges/BDG-SAH0001?businessDate={Today:yyyy-MM-dd}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "NMS badges are invisible to an NFMS register");
    }
}
