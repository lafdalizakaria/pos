using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Tests;

public class LedgerTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    private static Account NewAccount(decimal overdraft = 0m) =>
        new(Guid.CreateVersion7(), TestData.DinerId, Guid.CreateVersion7(), AccountType.Prepaid, overdraft);

    private static MovementRequest Req(MovementType type, decimal amount) => new(type, amount, Guid.CreateVersion7(), Now);

    [Fact]
    public void Balance_is_the_sum_of_movements_and_cache_follows()
    {
        var account = NewAccount();
        var movements = new List<AccountMovement>
        {
            account.Post(Req(MovementType.TopUp, 200m) with { PaymentMethod = PaymentMethod.Cash }),
            account.Post(Req(MovementType.Consumption, -25m)),
            account.Post(Req(MovementType.Subsidy, 10m)),
            account.Post(Req(MovementType.Refund, 25m)),
        };

        Ledger.ComputeBalance(movements).Should().Be(210m);
        account.CachedBalance.Should().Be(210m);
        movements[^1].BalanceAfter.Should().Be(210m);
    }

    [Fact]
    public void Debit_beyond_balance_and_overdraft_is_refused_and_leaves_cache_unchanged()
    {
        var account = NewAccount(overdraft: 10m);
        account.Post(Req(MovementType.TopUp, 20m));

        var act = () => account.Post(Req(MovementType.Consumption, -30.01m));

        act.Should().Throw<InsufficientFundsException>().Which.Available.Should().Be(30m);
        account.CachedBalance.Should().Be(20m);
    }

    [Fact]
    public void Debit_up_to_overdraft_limit_is_allowed()
    {
        var account = NewAccount(overdraft: 10m);
        account.Post(Req(MovementType.TopUp, 20m));

        account.Post(Req(MovementType.Consumption, -30m));

        account.CachedBalance.Should().Be(-10m);
        account.AvailableToSpend.Should().Be(0m);
    }

    [Fact]
    public void Offline_replay_is_recorded_but_flagged_when_it_exceeds_the_limit()
    {
        var account = NewAccount();
        account.Post(Req(MovementType.TopUp, 10m));

        var movement = account.Post(Req(MovementType.Consumption, -25m) with { IsOfflineReplay = true });

        movement.ExceededOverdraft.Should().BeTrue();
        account.CachedBalance.Should().Be(-15m);
    }

    [Theory]
    [InlineData(MovementType.TopUp, -10)]
    [InlineData(MovementType.TopUp, 0)]
    [InlineData(MovementType.Subsidy, -1)]
    [InlineData(MovementType.Consumption, 10)]
    [InlineData(MovementType.Correction, 0)]
    [InlineData(MovementType.Refund, 0)]
    [InlineData(MovementType.Transfer, 0)]
    public void Sign_must_match_movement_type(MovementType type, decimal amount)
    {
        var account = NewAccount(overdraft: 1000m);

        var act = () => account.Post(Req(type, amount));

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_movement_sign");
    }

    [Fact]
    public void Amounts_with_more_than_two_decimals_are_rejected()
    {
        var act = () => NewAccount().Post(Req(MovementType.TopUp, 10.005m));

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_amount");
    }

    [Fact]
    public void Reversal_creates_an_opposite_correction_referencing_the_original()
    {
        var account = NewAccount();
        var topUp = account.Post(Req(MovementType.TopUp, 100m));

        var reversal = account.Reverse(topUp, Guid.CreateVersion7(), Now, "admin@newrest.ma", "Erreur de saisie");

        reversal.Type.Should().Be(MovementType.Correction);
        reversal.Amount.Should().Be(-100m);
        reversal.ReversesMovementId.Should().Be(topUp.Id);
        reversal.PerformedBy.Should().Be("admin@newrest.ma");
        account.CachedBalance.Should().Be(0m);
    }

    [Fact]
    public void Reversal_cannot_overdraw_the_account()
    {
        var account = NewAccount();
        var topUp = account.Post(Req(MovementType.TopUp, 100m));
        account.Post(Req(MovementType.Consumption, -60m));

        var act = () => account.Reverse(topUp, Guid.CreateVersion7(), Now, "admin", "Erreur");

        act.Should().Throw<InsufficientFundsException>();
    }

    [Fact]
    public void Reversals_cannot_be_reversed_nor_target_another_account()
    {
        var account = NewAccount();
        var other = NewAccount();
        var topUp = account.Post(Req(MovementType.TopUp, 100m));
        var reversal = account.Reverse(topUp, Guid.CreateVersion7(), Now, "admin", "Erreur");

        FluentActions.Invoking(() => account.Reverse(reversal, Guid.CreateVersion7(), Now, "admin", "x"))
            .Should().Throw<DomainException>().Which.Code.Should().Be("cannot_reverse_reversal");
        FluentActions.Invoking(() => other.Reverse(topUp, Guid.CreateVersion7(), Now, "admin", "x"))
            .Should().Throw<DomainException>().Which.Code.Should().Be("movement_other_account");
    }

    [Fact]
    public void Inactive_account_cannot_be_debited_online()
    {
        var account = NewAccount();
        account.Post(Req(MovementType.TopUp, 50m));
        account.IsActive = false;

        FluentActions.Invoking(() => account.Post(Req(MovementType.Consumption, -5m)))
            .Should().Throw<DomainException>().Which.Code.Should().Be("account_inactive");
    }

    [Fact]
    public void Idempotency_key_is_required_and_overdraft_must_be_positive()
    {
        FluentActions.Invoking(() => NewAccount().Post(new MovementRequest(MovementType.TopUp, 10m, Guid.Empty, Now)))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => NewAccount(-1m)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Movement_records_its_context()
    {
        var account = NewAccount();
        var key = Guid.CreateVersion7();
        var ticketId = Guid.CreateVersion7();
        account.Post(Req(MovementType.TopUp, 50m));

        var movement = account.Post(new MovementRequest(MovementType.Consumption, -12.5m, key, Now)
        {
            TicketId = ticketId,
            RegisterId = TestData.RegisterId,
            OperatorId = TestData.OperatorId,
            Comment = "Déjeuner",
        });

        movement.Should().BeEquivalentTo(new
        {
            AccountId = account.Id,
            IdempotencyKey = key,
            TicketId = (Guid?)ticketId,
            RegisterId = (Guid?)TestData.RegisterId,
            OperatorId = (Guid?)TestData.OperatorId,
            Comment = "Déjeuner",
            OccurredAt = Now,
            BalanceAfter = 37.5m,
            IsOfflineReplay = false,
        });
    }
}
