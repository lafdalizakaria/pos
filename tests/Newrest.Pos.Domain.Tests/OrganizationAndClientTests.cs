using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Domain.Tests;

public class OrganizationAndClientTests
{
    private static readonly Pbkdf2PinHasher FastHasher = new(1_000);

    [Fact]
    public void Pin_hash_roundtrip_and_salting()
    {
        var hash1 = FastHasher.Hash("1234");
        var hash2 = FastHasher.Hash("1234");

        hash1.Should().StartWith("PBKDF2-SHA256$1000$").And.NotBe(hash2);
        FastHasher.Verify("1234", hash1).Should().BeTrue();
        FastHasher.Verify("1235", hash1).Should().BeFalse();
        FastHasher.Verify("1234", "garbage").Should().BeFalse();
        FastHasher.Verify("1234", "PBKDF2-SHA256$1000$!!!$!!!").Should().BeFalse();
        FastHasher.Verify("", hash1).Should().BeFalse();
        new Pbkdf2PinHasher().Hash("123456").Should().Contain("$600000$");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    public void Weak_or_malformed_pins_are_rejected(string pin) =>
        FluentActions.Invoking(() => FastHasher.Hash(pin)).Should().Throw<DomainException>();

    [Fact]
    public void Operator_is_locked_after_repeated_failures_then_unlocked_by_time()
    {
        var op = new Operator(Guid.CreateVersion7(), Guid.CreateVersion7(), "caiss01", "Fatima", "Zahra", OperatorRoles.Cashier, FastHasher.Hash("2468"));
        var policy = new PinLockoutPolicy(3, TimeSpan.FromMinutes(10));
        var now = TestData.Now;

        op.VerifyPin("0000", FastHasher, policy, now).Should().Be(PinVerificationResult.InvalidPin);
        op.VerifyPin("0000", FastHasher, policy, now).Should().Be(PinVerificationResult.InvalidPin);
        op.VerifyPin("0000", FastHasher, policy, now).Should().Be(PinVerificationResult.LockedOut);
        op.VerifyPin("2468", FastHasher, policy, now.AddMinutes(5)).Should().Be(PinVerificationResult.LockedOut);
        op.VerifyPin("2468", FastHasher, policy, now.AddMinutes(11)).Should().Be(PinVerificationResult.Success);
        op.FailedPinAttempts.Should().Be(0);

        op.IsActive = false;
        op.VerifyPin("2468", FastHasher, policy, now).Should().Be(PinVerificationResult.Inactive);
    }

    [Fact]
    public void Operator_roles_pin_change_and_unlock()
    {
        var op = new Operator(Guid.CreateVersion7(), Guid.CreateVersion7(), "resp01", "Karim", "Alaoui",
            OperatorRoles.Cashier | OperatorRoles.Supervisor, FastHasher.Hash("1111"));
        op.HasRole(OperatorRoles.Supervisor).Should().BeTrue();
        op.HasRole(OperatorRoles.Admin).Should().BeFalse();
        op.DisplayName.Should().Be("Karim Alaoui");

        op.VerifyPin("0000", FastHasher, new PinLockoutPolicy(1), TestData.Now).Should().Be(PinVerificationResult.LockedOut);
        op.Unlock();
        op.IsLockedOut(TestData.Now).Should().BeFalse();
        op.ChangePin("9999", FastHasher);
        op.VerifyPin("9999", FastHasher, new PinLockoutPolicy(), TestData.Now).Should().Be(PinVerificationResult.Success);

        FluentActions.Invoking(() => new Operator(Guid.CreateVersion7(), Guid.CreateVersion7(), "x", "a", "b", OperatorRoles.None, "h"))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Lost_badge_is_replaced_and_keeps_the_same_diner()
    {
        var dinerId = Guid.CreateVersion7();
        var badge = new Badge(Guid.CreateVersion7(), dinerId, "a1b2c3", TestData.Now);

        var replacement = badge.ReplaceAsLost("D4E5F6", TestData.Now.AddDays(1));

        badge.Status.Should().Be(BadgeStatus.Lost);
        badge.IsUsable.Should().BeFalse();
        badge.ReplacedByBadgeId.Should().Be(replacement.Id);
        replacement.DinerId.Should().Be(dinerId);
        replacement.IsUsable.Should().BeTrue();
        replacement.Number.Should().Be("D4E5F6");
        FluentActions.Invoking(() => badge.ReplaceAsLost("X", TestData.Now)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => replacement.ReplaceAsLost("d4e5f6", TestData.Now)).Should().Throw<DomainException>()
            .Which.Code.Should().Be("same_badge_number");
    }

    [Fact]
    public void Badge_block_and_unblock()
    {
        var badge = new Badge(Guid.CreateVersion7(), Guid.CreateVersion7(), "123", TestData.Now);
        FluentActions.Invoking(badge.Unblock).Should().Throw<DomainException>();

        badge.Block(TestData.Now);
        badge.Status.Should().Be(BadgeStatus.Blocked);
        badge.Unblock();
        badge.Status.Should().Be(BadgeStatus.Active);
        badge.DeactivatedAt.Should().BeNull();
    }

    [Fact]
    public void Contract_points_of_sale_validity_and_rules()
    {
        var contract = new Contract(Guid.CreateVersion7(), Guid.CreateVersion7(), "CTR-2026-001", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var pos = Guid.CreateVersion7();
        contract.AcceptPointOfSale(pos);
        contract.AcceptPointOfSale(pos);

        contract.PointsOfSale.Should().ContainSingle();
        contract.AcceptsPointOfSale(pos).Should().BeTrue();
        contract.AcceptsPointOfSale(Guid.CreateVersion7()).Should().BeFalse();
        contract.IsActiveOn(new DateOnly(2027, 1, 1)).Should().BeFalse();
        contract.IsActiveOn(TestData.Today).Should().BeTrue();
        contract.RemovePointOfSale(pos);
        contract.AcceptsPointOfSale(pos).Should().BeFalse();

        var rule = new SubsidyRule(Guid.CreateVersion7(), contract.Id, "Std", SubsidyKind.Percentage, 50m, new DateOnly(2026, 1, 1));
        contract.AddSubsidyRule(rule).Should().Be(rule);
        var foreign = new SubsidyRule(Guid.CreateVersion7(), Guid.CreateVersion7(), "X", SubsidyKind.Percentage, 50m, new DateOnly(2026, 1, 1));
        FluentActions.Invoking(() => contract.AddSubsidyRule(foreign)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => new Contract(Guid.CreateVersion7(), Guid.CreateVersion7(), "X", new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Organization_entities_normalize_codes_and_validate()
    {
        var company = new Company(Guid.CreateVersion7(), "nfms", "NFMS", "Newrest Food Management Services SA");
        var site = new Site(Guid.CreateVersion7(), company.Id, "cas-sm", "Sidi Maârouf", "Casablanca");
        var pos = new PointOfSale(Guid.CreateVersion7(), site.Id, "self1", "Self principal", PointOfSaleType.Self);
        var register = new Register(Guid.CreateVersion7(), pos.Id, "c01", "Caisse 1", "cas01");

        company.Code.Should().Be("NFMS");
        site.TimeZone.Should().Be("Africa/Casablanca");
        register.TicketPrefix.Should().Be("CAS01");
        FluentActions.Invoking(() => new Site(Guid.CreateVersion7(), Guid.Empty, "x", "x", "x")).Should().Throw<DomainException>();
        FluentActions.Invoking(() => new Company(Guid.CreateVersion7(), new string('X', 17), "x", "x")).Should().Throw<DomainException>()
            .Which.Code.Should().Be("too_long");
        new Diner(Guid.CreateVersion7(), Guid.CreateVersion7(), "M001", "Youssef", "Benali").DisplayName.Should().Be("Youssef Benali");
        new ClientCompany(Guid.CreateVersion7(), company.Id, "acme", "Acme").Code.Should().Be("ACME");
    }

    [Fact]
    public void Money_rounding_is_half_away_from_zero()
    {
        Money.Round(2.345m).Should().Be(2.35m);
        Money.Round(-2.345m).Should().Be(-2.35m);
        Money.VatFromInclusive(110m, 0.10m).Should().Be(10m);
        Money.VatFromInclusive(10m, 0m).Should().Be(0m);
        Money.HasAtMostTwoDecimals(1.1m).Should().BeTrue();
        Money.HasAtMostTwoDecimals(1.001m).Should().BeFalse();
    }
}
