using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Clients;

public enum SubsidyKind
{
    /// <summary><see cref="SubsidyRule.Value"/> is a percentage (50 = 50 %) of the eligible amount.</summary>
    Percentage,

    /// <summary><see cref="SubsidyRule.Value"/> is a fixed MAD amount per meal (never above the eligible amount).</summary>
    FixedAmount,
}

/// <summary>Employer contribution attached to a contract.</summary>
public sealed class SubsidyRule : ReferenceEntity
{
    private SubsidyRule()
    {
    }

    public SubsidyRule(Guid id, Guid contractId, string name, SubsidyKind kind, decimal value, DateOnly validFrom,
        decimal? maxPerMeal = null, decimal? maxPerDay = null, int? maxMealsPerDay = null,
        string? dinerCategory = null, DateOnly? validTo = null) : base(id)
    {
        ContractId = Guard.NotEmpty(contractId, nameof(contractId));
        Name = Guard.NotBlank(name, nameof(name), 100);
        Kind = kind;
        Value = kind switch
        {
            SubsidyKind.Percentage when value is < 0 or > 100 =>
                throw new DomainException("invalid_subsidy", "A percentage must be between 0 and 100."),
            SubsidyKind.FixedAmount => Money.EnsureValid(Guard.NotNegative(value, nameof(value)), nameof(value)),
            _ => value,
        };
        MaxPerMeal = maxPerMeal is { } m ? Money.EnsureValid(Guard.NotNegative(m, nameof(maxPerMeal)), nameof(maxPerMeal)) : null;
        MaxPerDay = maxPerDay is { } d ? Money.EnsureValid(Guard.NotNegative(d, nameof(maxPerDay)), nameof(maxPerDay)) : null;
        if (maxMealsPerDay is < 1)
        {
            throw new DomainException("invalid_subsidy", "Max meals per day must be at least 1.");
        }

        MaxMealsPerDay = maxMealsPerDay;
        DinerCategory = string.IsNullOrWhiteSpace(dinerCategory) ? null : dinerCategory.Trim().ToUpperInvariant();
        if (validTo is { } to && to < validFrom)
        {
            throw new DomainException("invalid_validity", "ValidTo must be on or after ValidFrom.");
        }

        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public Guid ContractId { get; private set; }
    public string Name { get; private set; } = null!;
    public SubsidyKind Kind { get; private set; }
    public decimal Value { get; private set; }
    public decimal? MaxPerMeal { get; private set; }
    public decimal? MaxPerDay { get; private set; }
    public int? MaxMealsPerDay { get; private set; }

    /// <summary>Restricts the rule to a diner category; null = every diner of the contract.</summary>
    public string? DinerCategory { get; private set; }

    public DateOnly ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }

    public bool IsValidOn(DateOnly date) => date >= ValidFrom && (ValidTo is null || date <= ValidTo);

    /// <summary>
    /// Rules are never edited in place (past tickets reference them): a change = close this rule and create a new one.
    /// </summary>
    public void Close(DateOnly lastValidDate)
    {
        if (lastValidDate < ValidFrom.AddDays(-1))
        {
            throw new DomainException("invalid_validity", "A rule cannot be closed before the day preceding its start.");
        }

        if (ValidTo is { } current && lastValidDate > current)
        {
            throw new DomainException("invalid_validity", "A closed rule cannot be extended; create a new rule.");
        }

        ValidTo = lastValidDate;
    }
}

/// <param name="TicketTotal">Total of the ticket, TTC.</param>
/// <param name="EligibleAmount">Part of the total made of subsidizable articles.</param>
/// <param name="SubsidyAlreadyGrantedToday">Sum of subsidies already granted to the diner on the business date, across ALL points of sale (net of credit notes).</param>
/// <param name="SubsidizedMealsToday">Number of already subsidized meals on the business date (net of credit notes).</param>
public readonly record struct SubsidyRequest(
    decimal TicketTotal,
    decimal EligibleAmount,
    decimal SubsidyAlreadyGrantedToday,
    int SubsidizedMealsToday);

public readonly record struct SubsidyResult(decimal EmployerShare, decimal DinerShare, SubsidyLimitation Limitation)
{
    public bool IsSubsidized => EmployerShare > 0;
}

public enum SubsidyLimitation
{
    None,
    NoRule,
    PerMealCap,
    DailyCap,
    DailyCapExhausted,
    MealCountExhausted,
}

public static class SubsidyCalculator
{
    public static SubsidyResult Calculate(SubsidyRule? rule, SubsidyRequest request)
    {
        var total = Money.EnsureValid(Guard.NotNegative(request.TicketTotal, nameof(request.TicketTotal)), nameof(request.TicketTotal));
        var eligible = Guard.NotNegative(request.EligibleAmount, nameof(request.EligibleAmount));
        if (eligible > total)
        {
            throw new DomainException("invalid_subsidy_request", "Eligible amount cannot exceed the ticket total.");
        }

        if (rule is null || eligible == 0)
        {
            return new SubsidyResult(0m, total, SubsidyLimitation.NoRule);
        }

        if (rule.MaxMealsPerDay is { } maxMeals && request.SubsidizedMealsToday >= maxMeals)
        {
            return new SubsidyResult(0m, total, SubsidyLimitation.MealCountExhausted);
        }

        var limitation = SubsidyLimitation.None;
        var amount = rule.Kind switch
        {
            SubsidyKind.Percentage => Money.Round(eligible * rule.Value / 100m),
            SubsidyKind.FixedAmount => Math.Min(rule.Value, eligible),
            _ => throw new DomainException("invalid_subsidy", $"Unsupported subsidy kind {rule.Kind}."),
        };

        if (rule.MaxPerMeal is { } perMeal && amount > perMeal)
        {
            amount = perMeal;
            limitation = SubsidyLimitation.PerMealCap;
        }

        if (rule.MaxPerDay is { } perDay)
        {
            var remaining = Math.Max(0m, perDay - Math.Max(0m, request.SubsidyAlreadyGrantedToday));
            if (remaining == 0m)
            {
                return new SubsidyResult(0m, total, SubsidyLimitation.DailyCapExhausted);
            }

            if (amount > remaining)
            {
                amount = remaining;
                limitation = SubsidyLimitation.DailyCap;
            }
        }

        amount = Money.Round(amount); // normalise the scale (rule values are stored with 4 decimals)
        return new SubsidyResult(amount, total - amount, limitation);
    }

    /// <summary>
    /// Selects the rule to apply: valid on the date, category-specific rules first, then generic ones;
    /// on a tie the most recent <see cref="SubsidyRule.ValidFrom"/> wins.
    /// </summary>
    public static SubsidyRule? SelectRule(IEnumerable<SubsidyRule> rules, DateOnly date, string? dinerCategory)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var category = string.IsNullOrWhiteSpace(dinerCategory) ? null : dinerCategory.Trim().ToUpperInvariant();
        return rules
            .Where(r => r.IsValidOn(date) && (r.DinerCategory is null || r.DinerCategory == category))
            .OrderByDescending(r => r.DinerCategory is not null)
            .ThenByDescending(r => r.ValidFrom)
            .FirstOrDefault();
    }
}
