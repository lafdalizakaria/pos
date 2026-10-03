using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Accounts;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Infrastructure.Seeding;

namespace Newrest.Pos.Infrastructure.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class LedgerConcurrencyTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync(seedDemo: true);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Guid DemoAccount(string number) => DemoDataSeeder.Id($"account:{number}");

    private async Task<LedgerPostResult> PostAsync(Guid accountId, MovementRequest request)
    {
        await using var db = _database.CreateContext();
        return await _database.CreateLedger(db).PostAsync(accountId, request);
    }

    private static MovementRequest Debit(decimal amount) =>
        new(MovementType.Consumption, -amount, Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Fifty_concurrent_debits_never_exceed_balance_plus_overdraft()
    {
        // ATL0004: Mixed account, 50 MAD balance + 200 MAD overdraft => 250 MAD available, 25 debits of 10 MAD.
        var accountId = DemoAccount("ATL0004");
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            try
            {
                await PostAsync(accountId, Debit(10m));
                return true;
            }
            catch (InsufficientFundsException)
            {
                return false;
            }
        })).ToList();
        start.Set();
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Count(ok => ok).Should().Be(25);
        await using var db = _database.CreateContext();
        var balance = await _database.CreateLedger(db).GetBalanceAsync(accountId);
        balance.LedgerBalance.Should().Be(-200m);
        balance.CachedBalance.Should().Be(balance.LedgerBalance);
        balance.AvailableToSpend.Should().Be(0m);
        (await db.AccountMovements.CountAsync(m => m.AccountId == accountId && m.Type == MovementType.Consumption)).Should().Be(25);
        (await _database.CreateLedger(db).FindCacheDriftAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_debits_on_different_accounts_all_succeed()
    {
        var accounts = new[] { "ATL0001", "ATL0002", "SAH0002" }.Select(DemoAccount).ToArray();

        await Task.WhenAll(accounts.SelectMany(a => Enumerable.Range(0, 10).Select(_ => PostAsync(a, Debit(1m)))));

        await using var db = _database.CreateContext();
        var ledger = _database.CreateLedger(db);
        (await ledger.GetBalanceAsync(accounts[0])).LedgerBalance.Should().Be(290m);
        (await ledger.GetBalanceAsync(accounts[1])).LedgerBalance.Should().Be(140m);
        (await ledger.GetBalanceAsync(accounts[2])).LedgerBalance.Should().Be(90m);
    }

    [Fact]
    public async Task Replayed_idempotency_key_returns_the_original_movement()
    {
        var accountId = DemoAccount("ATL0002");
        var request = Debit(12.50m);

        var first = await PostAsync(accountId, request);
        var replays = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostAsync(accountId, request)));

        first.WasDuplicate.Should().BeFalse();
        replays.Should().OnlyContain(r => r.WasDuplicate && r.MovementId == first.MovementId && r.BalanceAfter == 137.50m);
        await using var db = _database.CreateContext();
        (await db.AccountMovements.CountAsync(m => m.IdempotencyKey == request.IdempotencyKey)).Should().Be(1);
        (await _database.CreateLedger(db).GetBalanceAsync(accountId)).LedgerBalance.Should().Be(137.50m);
    }

    [Fact]
    public async Task Concurrent_first_submissions_with_the_same_key_create_a_single_movement()
    {
        var accountId = DemoAccount("ATL0001");
        var request = Debit(7m);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => PostAsync(accountId, request))));

        results.Count(r => !r.WasDuplicate).Should().Be(1);
        results.Select(r => r.MovementId).Distinct().Should().ContainSingle();
        await using var db = _database.CreateContext();
        (await _database.CreateLedger(db).GetBalanceAsync(accountId)).LedgerBalance.Should().Be(293m);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_movement_is_a_conflict()
    {
        var request = Debit(5m);
        await PostAsync(DemoAccount("SAH0002"), request);

        var act = () => PostAsync(DemoAccount("SAH0002"), request with { Amount = -6m });

        await act.Should().ThrowAsync<IdempotencyConflictException>();
    }

    [Fact]
    public async Task Reversal_is_applied_once_and_audited_by_reference()
    {
        var accountId = DemoAccount("SAH0003");
        var topUp = await PostAsync(accountId, new MovementRequest(MovementType.TopUp, 60m, Guid.NewGuid(), DateTimeOffset.UtcNow)
        { PaymentMethod = PaymentMethod.Cash, RegisterId = DemoDataSeeder.Id("register:KEN1") });

        await using (var db = _database.CreateContext())
        {
            var reversal = await _database.CreateLedger(db).ReverseAsync(topUp.MovementId, Guid.NewGuid(), "admin@newrest.ma", "Doublon");
            reversal.Amount.Should().Be(-60m);
            reversal.BalanceAfter.Should().Be(40m);
        }

        await using (var db = _database.CreateContext())
        {
            var again = () => _database.CreateLedger(db).ReverseAsync(topUp.MovementId, Guid.NewGuid(), "admin@newrest.ma", "Doublon");
            (await again.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be("already_reversed");
        }
    }

    [Fact]
    public async Task Offline_replay_beyond_limit_is_accepted_and_flagged_but_online_debit_is_refused()
    {
        var accountId = DemoAccount("ATL0003"); // prepaid, 20 MAD

        await FluentActions.Invoking(() => PostAsync(accountId, Debit(25m))).Should().ThrowAsync<InsufficientFundsException>();
        var offline = await PostAsync(accountId, Debit(25m) with { IsOfflineReplay = true });

        offline.ExceededOverdraft.Should().BeTrue();
        offline.BalanceAfter.Should().Be(-5m);
    }

    [Fact]
    public async Task Unknown_account_is_reported()
    {
        var act = () => PostAsync(Guid.NewGuid(), Debit(1m));

        await act.Should().ThrowAsync<AccountNotFoundException>();
    }
}
