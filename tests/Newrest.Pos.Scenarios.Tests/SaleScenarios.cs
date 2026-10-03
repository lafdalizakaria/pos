using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Scenarios.Tests;

[Collection(ScenarioCollection.Name)]
public sealed class SaleScenarios(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Cas1 = DemoDataSeeder.Id("register:CAS1");
    private TestDatabase _database = null!;
    private ApiFactory _api = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync(seedDemo: true);
        _api = new ApiFactory(_database.ConnectionString);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<T> GetAsync<T>(string url) => (await (await _api.Admin().GetAsync(url)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<T>())!;

    private static async Task DrainAsync(RegisterHarness register)
    {
        var sync = register.Get<SyncService>();
        for (var i = 0; i < 50 && await register.Get<LocalStore>().CountPendingAsync() > 0; i++)
        {
            await sync.RunOnceAsync();
        }

        (await register.Get<LocalStore>().CountPendingAsync()).Should().Be(0, register.Get<ConnectivityState>().Label);
    }

    [Fact]
    public async Task Online_badge_sale_with_subsidy_is_printed_synced_and_visible_in_back_office()
    {
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1);
        var (cashier, session) = await register.LoginAndOpenAsync();
        var sales = register.Get<SaleService>();

        var cart = await register.CartAsync(session, "CSC-VND", "EAU-50");
        cart.Diner = await sales.IdentifyBadgeAsync("bdg-atl0002", session.BusinessDate);
        cart.Diner.IsOnline.Should().BeTrue();
        cart.ComputeSubsidy(session.BusinessDate).EmployerShare.Should().Be(20m, "50 % of 42 capped at 20 MAD/day; water is not subsidizable");

        var result = await sales.CompleteSaleAsync(cart, [new PaymentChoice(PaymentMethod.Account, 27m)], cashier, session);

        result.Ticket.Number.Should().Be("CAS1-00000001");
        result.Ticket.TotalAmount.Should().Be(47m);
        result.AccountDebitedOffline.Should().BeFalse();
        result.PrintError.Should().BeNull();
        var printed = register.Printer.Printed.Single().ToPlainText();
        printed.Should().Contain("CAS1-00000001").And.Contain("Subvention employeur").And.Contain("Part convive").And.Contain("ICE 000000000000001")
            .And.Contain("Solde du compte").And.Contain("123,00");

        await DrainAsync(register);
        var tickets = await GetAsync<PagedResult<TicketSummaryDto>>($"/api/v1/tickets?registerId={Cas1}");
        tickets.Items.Should().ContainSingle().Which.Should().Match<TicketSummaryDto>(t =>
            t.SubsidyAmount == 20m && t.DinerShare == 27m && t.DinerName == "Khadija El Amrani");
        (await GetAsync<ChainVerificationDto>($"/api/v1/registers/{Cas1}/chain-verification")).IsValid.Should().BeTrue();
        (await GetAsync<AccountDto>($"/api/v1/accounts/{DemoDataSeeder.Id("account:ATL0002")}")).Balance.Should().Be(123m);
        var detail = await GetAsync<TicketDetailDto>($"/api/v1/tickets/{result.Ticket.Id}");
        detail.Hash.Should().Be(result.Ticket.Hash);
        detail.Payments.Should().ContainSingle(p => p.Method == "Account");
    }

    [Fact]
    public async Task Offline_twenty_sales_with_cap_then_recovery_without_duplicates()
    {
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1, new RegisterOptions { OfflineSpendingLimitPerBadge = 60m });
        var (cashier, session) = await register.LoginAndOpenAsync();
        await register.Get<SyncService>().RunOnceAsync();
        var sales = register.Get<SaleService>();
        register.Network.Mode = NetworkMode.Down;

        var accountSales = 0;
        var refusedByCap = 0;
        for (var i = 0; i < 20; i++)
        {
            var cart = await register.CartAsync(session, "CSC-PLT");
            if (i < 5)
            {
                cart.Diner = await sales.IdentifyBadgeAsync("BDG-ATL0001", session.BusinessDate); // Youssef, cadre: 25 MAD per day
                cart.Diner.IsOnline.Should().BeFalse();
                var due = cart.Total - cart.ComputeSubsidy(session.BusinessDate).EmployerShare;
                try
                {
                    await sales.CompleteSaleAsync(cart, [new PaymentChoice(PaymentMethod.Account, due)], cashier, session);
                    accountSales++;
                    continue;
                }
                catch (DomainException ex) when (ex.Code == "offline_limit")
                {
                    refusedByCap++;
                }

                await sales.CompleteSaleAsync(cart, [new PaymentChoice(PaymentMethod.Cash, due, 50m)], cashier, session);
                continue;
            }

            await sales.CompleteSaleAsync(cart, [new PaymentChoice(PaymentMethod.Cash, 38m, 40m)], cashier, session);
        }

        accountSales.Should().Be(2, "13 + 38 = 51 MAD fits under the 60 MAD offline cap, a third debit does not");
        refusedByCap.Should().Be(3);
        register.Get<ConnectivityState>().IsOnline.Should().BeFalse();
        (await register.Get<LocalStore>().CountPendingAsync()).Should().Be(22, "20 tickets + 2 offline debits (session already sent)");
        register.Printer.Printed.Should().HaveCount(20);

        // Network comes back flaky: requests processed but responses lost, then a clean network.
        register.Network.Mode = NetworkMode.LoseResponses;
        for (var i = 0; i < 5; i++)
        {
            await register.Get<SyncService>().RunOnceAsync();
        }

        register.Network.Mode = NetworkMode.Up;
        await DrainAsync(register);

        await using var db = _database.CreateContext();
        (await db.Tickets.CountAsync(t => t.RegisterId == Cas1)).Should().Be(20, "no ticket lost nor duplicated");
        (await db.Tickets.Where(t => t.RegisterId == Cas1).Select(t => t.Sequence).OrderBy(s => s).ToListAsync()).Should().Equal(Enumerable.Range(1, 20).Select(i => (long)i));
        var debits = await db.AccountMovements.Where(m => m.AccountId == DemoDataSeeder.Id("account:ATL0001") && m.Type == MovementType.Consumption).ToListAsync();
        debits.Should().HaveCount(2).And.OnlyContain(m => m.IsOfflineReplay);
        debits.Sum(m => m.Amount).Should().Be(-51m);
        (await GetAsync<AccountDto>($"/api/v1/accounts/{DemoDataSeeder.Id("account:ATL0001")}")).Balance.Should().Be(249m);
        (await GetAsync<ChainVerificationDto>($"/api/v1/registers/{Cas1}/chain-verification")).Should().Match<ChainVerificationDto>(c => c.IsValid && c.TicketsChecked == 20);
        (await db.Tickets.Where(t => t.RegisterId == Cas1).SumAsync(t => t.SubsidyAmount)).Should().Be(25m, "the daily cap was applied offline too");
    }

    [Fact]
    public async Task Credit_note_top_up_and_Z_report_close_the_day_on_both_sides()
    {
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1);
        var (cashier, session) = await register.LoginAndOpenAsync(openingFloat: 300m);
        var sales = register.Get<SaleService>();
        var operations = register.Get<AccountOperationsService>();

        await sales.CompleteSaleAsync(await register.CartAsync(session, "SAL-MAR", "FRU-SAI"), [new PaymentChoice(PaymentMethod.Cash, 15m, 20m)], cashier, session);
        var cart = await register.CartAsync(session, "TAJ-PLT");
        cart.Diner = await sales.IdentifyBadgeAsync("BDG-ATL0004", session.BusinessDate); // Nadia, mixed account
        var accountSale = await sales.CompleteSaleAsync(cart, [new PaymentChoice(PaymentMethod.Account, 19m)], cashier, session);
        var topUp = await operations.TopUpAsync(cart.Diner, 100m, PaymentMethod.Cash, cashier, session);
        topUp.BalanceAfter.Should().Be(50m - 19m + 100m);

        await FluentActions.Invoking(() => operations.IssueCreditNoteAsync(accountSale.Ticket.Id, "Plat froid", cashier, session))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == "forbidden");
        var login = register.Get<OperatorLoginService>();
        (await login.LoginAsync("RESP01", "5678")).Should().Be(LoginOutcome.Success);
        var credit = await operations.IssueCreditNoteAsync(accountSale.Ticket.Id, "Plat froid", login.Current!, session);
        credit.Ticket.TotalAmount.Should().Be(-38m);
        credit.Ticket.SubsidyAmount.Should().Be(-19m);
        await FluentActions.Invoking(() => operations.IssueCreditNoteAsync(accountSale.Ticket.Id, "Bis", login.Current!, session))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == "already_credited");

        var sessions = register.Get<CashSessionService>();
        var preview = await sessions.PreviewAsync(0m);
        preview.ExpectedCash.Should().Be(300m + 15m + 100m);
        var z = await sessions.CloseAsync(login.Current!.Id, countedCash: 410m);
        z.CashDifference.Should().Be(-5m);
        z.NetSales.Should().Be(15m);
        register.Printer.Printed.Last().ToPlainText().Should().Contain("CLÔTURE Z N° 1").And.Contain("Recharges de comptes (hors CA)");
        await DrainAsync(register);

        var reports = await GetAsync<List<ZReportDto>>($"/api/v1/z-reports?registerId={Cas1}");
        reports.Should().ContainSingle().Which.Should().Match<ZReportDto>(r =>
            r.ZNumber == 1 && r.NetSales == 15m && r.CashDifference == -5m && r.AccountTopUpTotal == 100m && r.CreditNoteCount == 1);
        (await GetAsync<AccountDto>($"/api/v1/accounts/{DemoDataSeeder.Id("account:ATL0004")}")).Balance.Should().Be(150m);
        var tickets = await GetAsync<PagedResult<TicketSummaryDto>>($"/api/v1/tickets?registerId={Cas1}");
        tickets.Items.Single(t => t.Id == accountSale.Ticket.Id).IsCredited.Should().BeTrue();

        (await sessions.GetOpenSessionAsync()).Should().BeNull();
        await sessions.OpenAsync(cashier.Id, 300m);
        await DrainAsync(register);
    }

    [Fact]
    public async Task Lost_badge_is_refused_after_sync_and_login_rules_apply_offline()
    {
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1);
        var login = register.Get<OperatorLoginService>();
        (await login.LoginAsync("NOBODY", "1234")).Should().Be(LoginOutcome.UnknownOperator);
        (await login.LoginAsync("CAIS02", "1234")).Should().Be(LoginOutcome.UnknownOperator, "CAIS02 works in Tanger");
        for (var i = 0; i < 4; i++)
        {
            (await login.LoginAsync("RESP01", "0000")).Should().Be(LoginOutcome.InvalidPin);
        }

        (await login.LoginAsync("RESP01", "0000")).Should().Be(LoginOutcome.LockedOut);
        (await login.LoginAsync("RESP01", "5678")).Should().Be(LoginOutcome.LockedOut);

        var (_, session) = await register.LoginAndOpenAsync();
        var badge = (await GetAsync<DinerDto>("/api/v1/diners/by-badge/BDG-ATL0003")).Badges.Single();
        (await _api.Admin().PostAsJsonAsync($"/api/v1/badges/{badge.Id}/lost", new BadgeReplace("BDG-ATL0003-NEW"))).EnsureSuccessStatusCode();
        await register.Get<SyncService>().RunOnceAsync(forceReference: true);
        register.Network.Mode = NetworkMode.Down;

        var sales = register.Get<SaleService>();
        await FluentActions.Invoking(() => sales.IdentifyBadgeAsync("BDG-ATL0003", session.BusinessDate))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == "badge_refused" && e.Message.Contains("perdu"));
        var replacement = await sales.IdentifyBadgeAsync("BDG-ATL0003-NEW", session.BusinessDate);
        replacement.IsOnline.Should().BeFalse();
        replacement.Available.Should().Be(20m, "the balance stayed on the account");
        await FluentActions.Invoking(() => sales.IdentifyBadgeAsync("UNKNOWN-1", session.BusinessDate))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == "badge_unknown");
    }

    [Fact]
    public async Task Reinstalled_register_continues_the_chain()
    {
        var first = await RegisterHarness.CreateAsync(_api, Cas1);
        var (cashier, session) = await first.LoginAndOpenAsync();
        var sales = first.Get<SaleService>();
        for (var i = 0; i < 2; i++)
        {
            await sales.CompleteSaleAsync(await first.CartAsync(session, "PAIN"), [new PaymentChoice(PaymentMethod.Cash, 1.5m)], cashier, session);
        }

        var z = await first.Get<CashSessionService>().CloseAsync(cashier.Id, 203m);
        await DrainAsync(first);
        await first.DisposeAsync();

        await using var second = await RegisterHarness.CreateAsync(_api, Cas1);
        var (cashier2, session2) = await second.LoginAndOpenAsync();
        var ticket = await second.Get<SaleService>().CompleteSaleAsync(await second.CartAsync(session2, "PAIN"),
            [new PaymentChoice(PaymentMethod.Cash, 1.5m)], cashier2, session2);
        ticket.Ticket.Sequence.Should().Be(3);
        (await second.Get<CashSessionService>().CloseAsync(cashier2.Id, 201.5m)).ZNumber.Should().Be(z.ZNumber + 1);
        await DrainAsync(second);
        (await GetAsync<ChainVerificationDto>($"/api/v1/registers/{Cas1}/chain-verification")).Should()
            .Match<ChainVerificationDto>(c => c.IsValid && c.TicketsChecked == 3);
    }

    [Fact]
    public async Task Sale_screen_takes_three_gestures_for_a_standard_tray()
    {
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1);
        await register.LoginAndOpenAsync();
        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();
        screen.Error.Should().BeNull();
        screen.Groups.Should().NotBeEmpty();
        screen.MenuLabel.Should().StartWith("Menu");

        var couscous = screen.Groups.SelectMany(g => g.Items).Single(i => i.Item.ArticleCode == "CSC-VND");
        screen.AddItemCommand.Execute(couscous);                                       // 1. tray (manual here, vision in phase 4)
        await screen.ScanBadgeCommand.ExecuteAsync("BDG-ATL0001");                     // 2. badge
        screen.Subsidy.Should().Be(25m);
        screen.CanPayWithAccount.Should().BeTrue();
        await screen.PayWithAccountCommand.ExecuteAsync(null);                         // 3. pay

        screen.Error.Should().BeNull();
        screen.Message.Should().StartWith("Ticket CAS1-00000001 — 17,00 MAD").And.NotContain("hors ligne");
        screen.Cart.IsEmpty.Should().BeTrue("a new cart is ready");

        screen.Search = "couscous";
        screen.Groups.SelectMany(g => g.Items).Should().OnlyContain(i => i.Label.Contains("Couscous"));
        var water = screen.Groups.Count;
        screen.Search = "";
        screen.Groups.Count.Should().BeGreaterThan(water);
    }

    [Fact]
    public async Task Permanent_refusal_blocks_the_queue_until_a_supervisor_retries()
    {
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1);
        var (cashier, session) = await register.LoginAndOpenAsync();
        await register.Get<SyncService>().RunOnceAsync();

        // The register is deactivated in the back-office while it still sells (its token is still valid).
        var registers = await GetAsync<List<RegisterDto>>($"/api/v1/registers?pointOfSaleId={DemoDataSeeder.Id("pos:CAS-SELF")}");
        var cas1 = registers.Single(r => r.Id == Cas1);
        (await _api.Admin().PutAsJsonAsync($"/api/v1/registers/{Cas1}", new RegisterUpsert(cas1.PointOfSaleId, cas1.Code, cas1.Name, cas1.TicketPrefix, false)))
            .EnsureSuccessStatusCode();
        await register.Get<SaleService>().CompleteSaleAsync(await register.CartAsync(session, "PAIN"), [new PaymentChoice(PaymentMethod.Cash, 1.5m)], cashier, session);
        await register.Get<SyncService>().RunOnceAsync();

        var status = register.Get<ConnectivityState>();
        status.BlockingError.Should().Contain("Ticket refusé");
        status.Label.Should().StartWith("Synchronisation bloquée");
        status.PendingCount.Should().Be(1);
        await register.Get<SyncService>().RunOnceAsync();
        status.PendingCount.Should().Be(1, "a refused fiscal item is never skipped");

        (await _api.Admin().PutAsJsonAsync($"/api/v1/registers/{Cas1}", new RegisterUpsert(cas1.PointOfSaleId, cas1.Code, cas1.Name, cas1.TicketPrefix, true)))
            .EnsureSuccessStatusCode();
        await register.Get<SyncService>().RetryRejectedAsync();
        await DrainAsync(register);
        status.BlockingError.Should().BeNull();
        (await GetAsync<ChainVerificationDto>($"/api/v1/registers/{Cas1}/chain-verification")).TicketsChecked.Should().Be(1);
    }
}

