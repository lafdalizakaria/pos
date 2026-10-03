using CommunityToolkit.Mvvm.ComponentModel;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Client.Core.Sales;

public sealed partial class CartLine(Guid articleId, string code, string label, Guid categoryId, decimal unitPrice, decimal vatRate, bool isSubsidizable,
    LineSource source) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount))]
    private int _quantity = 1;

    [ObservableProperty]
    private LineSource _source = source;

    /// <summary>Recognised with a medium confidence: highlighted until confirmed or switched to the second choice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlternative))]
    private bool _needsReview;

    /// <summary>Second choice proposed by the vision service (one tap to switch).</summary>
    public Data.MenuButton? Alternative { get; set; }

    public decimal? Confidence { get; set; }

    public bool HasAlternative => NeedsReview && Alternative is not null;

    public string? ReviewText => Confidence is { } c ? $"Reconnu à {c * 100:0} %" + (Alternative is { } a ? $" — sinon {a.Label} ?" : "") : null;

    /// <summary>Predictions of the vision service this line comes from (index in the response, and how it was validated).</summary>
    public List<(int Index, LineSource Source)> Predictions { get; } = [];

    public Guid ArticleId { get; } = articleId;
    public string Code { get; } = code;
    public string Label { get; } = label;
    public Guid CategoryId { get; } = categoryId;
    public decimal UnitPrice { get; } = unitPrice;
    public decimal VatRate { get; } = vatRate;
    public bool IsSubsidizable { get; } = isSubsidizable;

    public decimal Amount => Money.Round(UnitPrice * Quantity);

    public TicketLineInput ToInput() => new(ArticleId, Code, Label, Quantity, UnitPrice, VatRate, IsSubsidizable, Source);
}

/// <summary>Diner identified by badge, with what is needed to compute the subsidy and pay from the account.</summary>
/// <param name="IsOnline">True when the context comes from the server (fresh balance and daily subsidy on all points of sale).</param>
/// <param name="Available">Balance + overdraft available (server value online, cached minus local pending debits offline).</param>
public sealed record DinerContext(
    SyncDinerDto Diner, SyncBadgeDto Badge, SyncAccountDto? Account, bool AcceptedHere, decimal SubsidyGrantedToday, int SubsidizedMealsToday,
    IReadOnlyList<SubsidyRuleDto> Rules, bool IsOnline, decimal Available)
{
    public string DisplayName => $"{Diner.FirstName} {Diner.LastName}";

    public bool CanPayWithAccount => AcceptedHere && Account is { IsActive: true };

    public SubsidyRule? SelectRule(DateOnly date)
    {
        if (!AcceptedHere || Account is null)
        {
            return null;
        }

        var rules = Rules.Where(r => r.ContractId == Account.ContractId).Select(r => new SubsidyRule(r.Id, r.ContractId, r.Name,
            Enum.Parse<SubsidyKind>(r.Kind), r.Value, r.ValidFrom, r.MaxPerMeal, r.MaxPerDay, r.MaxMealsPerDay, r.DinerCategory, r.ValidTo));
        return SubsidyCalculator.SelectRule(rules, date, Diner.Category);
    }
}

/// <summary>Sale being prepared. The ticket id is fixed at creation: it is the idempotency key of every related operation.</summary>
public sealed partial class Cart : ObservableObject
{
    public Guid TicketId { get; } = Guid.CreateVersion7();

    public System.Collections.ObjectModel.ObservableCollection<CartLine> Lines { get; } = [];

    [ObservableProperty]
    private DinerContext? _diner;

    public decimal Total => Lines.Sum(l => l.Amount);

    public decimal EligibleAmount => Lines.Where(l => l.IsSubsidizable).Sum(l => l.Amount);

    public bool IsEmpty => Lines.Count == 0 || Lines.All(l => l.Quantity == 0);

    /// <param name="separate">Always a new line (a line to review keeps its own highlight and second choice).</param>
    public CartLine Add(DailyMenuItemDto item, decimal vatRate, bool isSubsidizable, LineSource source = LineSource.Manual, bool separate = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        var existing = separate ? null : Lines.FirstOrDefault(l => l.ArticleId == item.ArticleId && l.UnitPrice == item.EffectivePrice && !l.NeedsReview);
        if (existing is not null)
        {
            existing.Quantity++;
            return existing;
        }

        var line = new CartLine(item.ArticleId, item.ArticleCode, item.ArticleName, item.CategoryId, item.EffectivePrice, vatRate, isSubsidizable, source);
        Lines.Add(line);
        return line;
    }

    /// <summary>Employer share for this cart (0 without an eligible diner).</summary>
    public SubsidyResult ComputeSubsidy(DateOnly businessDate)
    {
        var total = Total;
        var rule = Diner?.SelectRule(businessDate);
        return SubsidyCalculator.Calculate(rule, new SubsidyRequest(total, EligibleAmount, Diner?.SubsidyGrantedToday ?? 0m, Diner?.SubsidizedMealsToday ?? 0));
    }
}
