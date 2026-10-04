using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Tests;

public class SubsidyCalculatorTests
{
    private static readonly Guid ContractId = Guid.CreateVersion7();

    private static SubsidyRule Rule(SubsidyKind kind, decimal value, decimal? perMeal = null, decimal? perDay = null, int? meals = null,
        string? category = null, DateOnly? from = null, DateOnly? to = null) =>
        new(Guid.CreateVersion7(), ContractId, "Règle", kind, value, from ?? new DateOnly(2026, 1, 1), perMeal, perDay, meals, category, to);

    [Fact]
    public void Reference_example_45_mad_with_50_percent_capped_20_per_day()
    {
        var rule = Rule(SubsidyKind.Percentage, 50m, perDay: 20m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(45m, 45m, 0m, 0));

        result.EmployerShare.Should().Be(20m);
        result.DinerShare.Should().Be(25m);
        result.Limitation.Should().Be(SubsidyLimitation.DailyCap);
    }

    [Fact]
    public void Daily_cap_accounts_for_meals_already_taken_on_other_points_of_sale()
    {
        var rule = Rule(SubsidyKind.Percentage, 50m, perDay: 20m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(30m, 30m, 12m, 1));

        result.EmployerShare.Should().Be(8m);
        result.DinerShare.Should().Be(22m);
    }

    [Fact]
    public void Exhausted_daily_cap_gives_no_subsidy()
    {
        var rule = Rule(SubsidyKind.Percentage, 50m, perDay: 20m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(30m, 30m, 20m, 1));

        result.EmployerShare.Should().Be(0m);
        result.DinerShare.Should().Be(30m);
        result.Limitation.Should().Be(SubsidyLimitation.DailyCapExhausted);
        result.IsSubsidized.Should().BeFalse();
    }

    [Fact]
    public void Credit_notes_can_make_already_granted_negative_and_never_increase_the_cap()
    {
        var rule = Rule(SubsidyKind.FixedAmount, 15m, perDay: 20m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(40m, 40m, -5m, 0));

        result.EmployerShare.Should().Be(15m);
    }

    [Fact]
    public void Per_meal_cap_applies_before_daily_cap()
    {
        var rule = Rule(SubsidyKind.Percentage, 80m, perMeal: 25m, perDay: 40m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(50m, 50m, 0m, 0));

        result.EmployerShare.Should().Be(25m);
        result.Limitation.Should().Be(SubsidyLimitation.PerMealCap);
    }

    [Fact]
    public void Result_has_a_two_decimal_scale_even_with_four_decimal_rule_values()
    {
        var rule = Rule(SubsidyKind.FixedAmount, 25.0000m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(38m, 38m, 0m, 0));

        result.EmployerShare.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be("25.00");
    }

    [Fact]
    public void Fixed_amount_never_exceeds_eligible_amount()
    {
        var rule = Rule(SubsidyKind.FixedAmount, 30m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(25m, 18m, 0m, 0));

        result.EmployerShare.Should().Be(18m);
        result.DinerShare.Should().Be(7m);
    }

    [Fact]
    public void Only_subsidizable_amount_is_used_as_base()
    {
        var rule = Rule(SubsidyKind.Percentage, 50m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(45m, 40m, 0m, 0));

        result.EmployerShare.Should().Be(20m);
        result.DinerShare.Should().Be(25m);
    }

    [Fact]
    public void Percentage_is_rounded_half_away_from_zero()
    {
        var rule = Rule(SubsidyKind.Percentage, 33m);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(10.50m, 10.50m, 0m, 0));

        result.EmployerShare.Should().Be(3.47m); // 3.465 -> 3.47
        (result.EmployerShare + result.DinerShare).Should().Be(10.50m);
    }

    [Fact]
    public void Max_meals_per_day_stops_subsidy()
    {
        var rule = Rule(SubsidyKind.Percentage, 50m, meals: 1);

        var result = SubsidyCalculator.Calculate(rule, new SubsidyRequest(30m, 30m, 15m, 1));

        result.EmployerShare.Should().Be(0m);
        result.Limitation.Should().Be(SubsidyLimitation.MealCountExhausted);
    }

    [Fact]
    public void No_rule_or_zero_ticket_means_diner_pays_everything()
    {
        SubsidyCalculator.Calculate(null, new SubsidyRequest(45m, 45m, 0m, 0)).DinerShare.Should().Be(45m);
        SubsidyCalculator.Calculate(Rule(SubsidyKind.FixedAmount, 10m), new SubsidyRequest(0m, 0m, 0m, 0)).EmployerShare.Should().Be(0m);
    }

    [Fact]
    public void Full_percentage_subsidy_leaves_nothing_to_pay()
    {
        var result = SubsidyCalculator.Calculate(Rule(SubsidyKind.Percentage, 100m), new SubsidyRequest(32.40m, 32.40m, 0m, 0));

        result.EmployerShare.Should().Be(32.40m);
        result.DinerShare.Should().Be(0m);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100.01)]
    public void Invalid_percentage_is_rejected(decimal value)
    {
        var act = () => Rule(SubsidyKind.Percentage, value);

        act.Should().Throw<DomainException>().Which.Code.Should().Be("invalid_subsidy");
    }

    [Fact]
    public void Invalid_requests_are_rejected()
    {
        var rule = Rule(SubsidyKind.Percentage, 50m);

        FluentActions.Invoking(() => SubsidyCalculator.Calculate(rule, new SubsidyRequest(10m, 11m, 0m, 0)))
            .Should().Throw<DomainException>().Which.Code.Should().Be("invalid_subsidy_request");
        FluentActions.Invoking(() => SubsidyCalculator.Calculate(rule, new SubsidyRequest(-1m, 0m, 0m, 0)))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => SubsidyCalculator.Calculate(rule, new SubsidyRequest(10.001m, 0m, 0m, 0)))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => Rule(SubsidyKind.FixedAmount, 10m, meals: 0)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => Rule(SubsidyKind.FixedAmount, 10m, from: new DateOnly(2026, 2, 1), to: new DateOnly(2026, 1, 1)))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Rule_selection_prefers_category_then_most_recent()
    {
        var generic = Rule(SubsidyKind.Percentage, 30m, from: new DateOnly(2026, 1, 1));
        var genericNewer = Rule(SubsidyKind.Percentage, 40m, from: new DateOnly(2026, 6, 1));
        var cadre = Rule(SubsidyKind.Percentage, 60m, category: "cadre", from: new DateOnly(2026, 1, 1));
        var expired = Rule(SubsidyKind.Percentage, 90m, from: new DateOnly(2025, 1, 1), to: new DateOnly(2025, 12, 31));
        SubsidyRule[] rules = [generic, genericNewer, cadre, expired];

        SubsidyCalculator.SelectRule(rules, new DateOnly(2026, 10, 5), "CADRE").Should().Be(cadre);
        SubsidyCalculator.SelectRule(rules, new DateOnly(2026, 10, 5), "OPERATEUR").Should().Be(genericNewer);
        SubsidyCalculator.SelectRule(rules, new DateOnly(2026, 3, 1), null).Should().Be(generic);
        SubsidyCalculator.SelectRule(rules, new DateOnly(2024, 3, 1), null).Should().BeNull();
    }
}
