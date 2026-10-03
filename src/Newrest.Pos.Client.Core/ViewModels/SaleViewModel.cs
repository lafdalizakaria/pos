using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newrest.Pos.Client.Core.Data;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Devices.Display;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Client.Core.Data
{
    public sealed record MenuButton(DailyMenuItemDto Item, string Label, string Price, decimal VatRate, bool IsSubsidizable, string? Color);

    public sealed record MenuCategoryGroup(string Name, string? Color, IReadOnlyList<MenuButton> Items);
}

namespace Newrest.Pos.Client.Core.ViewModels
{
    /// <summary>
    /// Sale screen: menu of the day by category (+ search), cart, badge, totals, then payment. A standard tray takes
    /// three gestures: items (or recognition, phase 4), badge, "Compte".
    /// </summary>
    public sealed partial class SaleViewModel(
        SaleService sales, ReferenceCache cache, CashSessionService sessions, OperatorLoginService login, ICustomerDisplay display, TimeProvider clock)
        : PageViewModel, IBadgeAware
    {
        private IReadOnlyList<MenuCategoryGroup> _allGroups = [];
        private LocalCashSession? _session;

        [ObservableProperty]
        private Cart _cart = new();

        [ObservableProperty]
        private IReadOnlyList<MenuCategoryGroup> _groups = [];

        [ObservableProperty]
        private string _search = "";

        [ObservableProperty]
        private string _menuLabel = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Remaining), nameof(Change), nameof(CanPayWithAccount), nameof(DinerLine))]
        private decimal _subsidy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Remaining), nameof(Change))]
        private decimal _total;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Change))]
        private decimal? _tendered;

        [ObservableProperty]
        private string _cardAuthorization = "";

        [ObservableProperty]
        private SaleResult? _lastSale;

        public ObservableCollection<PaymentChoice> Payments { get; } = [];

        public override string Title => "Vente";

        public decimal Due => Total - Subsidy;

        public decimal Remaining => Due - Payments.Sum(p => p.Amount);

        public decimal? Change => Tendered is { } t && t >= Remaining ? t - Remaining : null;

        public bool CanPayWithAccount => Cart.Diner?.CanPayWithAccount == true;

        public string? DinerLine => Cart.Diner is { } d
            ? $"{d.DisplayName} — {(d.CanPayWithAccount ? $"disponible {Format.Mad(d.Available)}" : "compte non accepté ici")}{(d.IsOnline ? "" : " (hors ligne)")}"
              + (Subsidy > 0 ? $" — subvention {Format.Mad(Subsidy)}" : string.Empty)
            : null;

        public async Task LoadAsync()
        {
            await RunAsync(async () =>
            {
                _session = await sessions.GetOpenSessionAsync() ?? throw new Domain.Common.DomainException("no_session", "Aucune session ouverte.");
                var menus = await cache.GetMenusAsync(_session.BusinessDate);
                var menu = PickMenu(menus, clock.GetLocalNow().TimeOfDay);
                var articles = await cache.GetArticlesAsync();
                var categories = (await cache.GetCategoriesAsync()).ToDictionary(c => c.Id);
                MenuLabel = menu is null ? "Aucun menu publié pour aujourd'hui" : $"Menu {ServiceLabel(menu.Service)} du {menu.Date:dd/MM}";
                _allGroups = menu is null ? [] : [.. menu.Items.Where(i => i.IsAvailable)
                    .GroupBy(i => i.CategoryId)
                    .OrderBy(g => categories.GetValueOrDefault(g.Key)?.DisplayOrder ?? 99)
                    .Select(g => new MenuCategoryGroup(categories.GetValueOrDefault(g.Key)?.Name ?? "Autres", categories.GetValueOrDefault(g.Key)?.ColorHex,
                        [.. g.OrderBy(i => i.DisplayOrder).Select(i => new MenuButton(i, i.ArticleName, Format.Amount(i.EffectivePrice),
                            articles.GetValueOrDefault(i.ArticleId)?.VatRate ?? 0.10m, articles.GetValueOrDefault(i.ArticleId)?.IsSubsidizable ?? true,
                            categories.GetValueOrDefault(g.Key)?.ColorHex))]))];
                ApplySearch();
            });
            NewCart();
        }

        /// <summary>Service shown by default: breakfast before 10:30, lunch before 16:00, dinner after; "all day" menus always qualify.</summary>
        public static DailyMenuDto? PickMenu(IReadOnlyList<DailyMenuDto> menus, TimeSpan now)
        {
            var wanted = now < new TimeSpan(10, 30, 0) ? "Breakfast" : now < new TimeSpan(16, 0, 0) ? "Lunch" : "Dinner";
            return menus.FirstOrDefault(m => m.Service == wanted) ?? menus.FirstOrDefault(m => m.Service == "AllDay") ?? menus.FirstOrDefault();
        }

        private static string ServiceLabel(string s) => s switch { "Breakfast" => "petit-déjeuner", "Lunch" => "midi", "Dinner" => "soir", _ => "journée" };

        partial void OnSearchChanged(string value) => ApplySearch();

        private void ApplySearch() => Groups = string.IsNullOrWhiteSpace(Search)
            ? _allGroups
            : [.. _allGroups.Select(g => g with { Items = [.. g.Items.Where(i => i.Label.Contains(Search, StringComparison.OrdinalIgnoreCase)
                    || i.Item.ArticleCode.Contains(Search, StringComparison.OrdinalIgnoreCase))] })
                .Where(g => g.Items.Count > 0)];

        [RelayCommand]
        private void AddItem(MenuButton button)
        {
            Cart.Add(button.Item, button.VatRate, button.IsSubsidizable);
            Recompute();
        }

        [RelayCommand]
        private void Increment(CartLine line)
        {
            line.Quantity++;
            Recompute();
        }

        [RelayCommand]
        private void Decrement(CartLine line)
        {
            line.Quantity--;
            if (line.Quantity <= 0)
            {
                Cart.Lines.Remove(line);
            }

            Recompute();
        }

        [RelayCommand]
        private void NewCart()
        {
            Cart = new Cart();
            Payments.Clear();
            Tendered = null;
            CardAuthorization = string.Empty;
            Recompute();
            _ = display.ShowAsync(CustomerDisplayState.Welcome);
        }

        public void OnBadgeScanned(string number) => _ = ScanBadgeAsync(number);

        [RelayCommand]
        public async Task ScanBadgeAsync(string number)
        {
            if (_session is null)
            {
                return;
            }

            await RunAsync(async () =>
            {
                Cart.Diner = await sales.IdentifyBadgeAsync(number, _session.BusinessDate);
                Recompute();
            });
        }

        [RelayCommand]
        private void ClearDiner()
        {
            Cart.Diner = null;
            Recompute();
        }

        /// <summary>One tap: the remaining amount is paid from the diner's account and the ticket is issued.</summary>
        [RelayCommand]
        private async Task PayWithAccountAsync()
        {
            if (Remaining > 0)
            {
                Payments.Add(new PaymentChoice(PaymentMethod.Account, Remaining));
            }

            if (!await CompleteAsync())
            {
                Payments.Remove(Payments.Last(p => p.Method == PaymentMethod.Account));
                Recompute();
            }
        }

        /// <summary>Cash: tendered amount (empty = exact amount). Completes the sale when it covers the remainder.</summary>
        [RelayCommand]
        private async Task PayCashAsync()
        {
            var remaining = Remaining;
            var tendered = Tendered ?? remaining;
            if (tendered < remaining)
            {
                Payments.Add(new PaymentChoice(PaymentMethod.Cash, tendered, tendered));
                Tendered = null;
                Recompute();
                return;
            }

            Payments.Add(new PaymentChoice(PaymentMethod.Cash, remaining, tendered));
            if (!await CompleteAsync())
            {
                Payments.RemoveAt(Payments.Count - 1);
                Recompute();
            }
        }

        /// <summary>Standalone terminal: amount typed on the terminal, authorisation reference typed here.</summary>
        [RelayCommand]
        private async Task PayCardAsync()
        {
            if (string.IsNullOrWhiteSpace(CardAuthorization))
            {
                Error = "Saisir la référence d'autorisation du TPE.";
                return;
            }

            Payments.Add(new PaymentChoice(PaymentMethod.Card, Remaining, AuthorizationCode: CardAuthorization));
            if (!await CompleteAsync())
            {
                Payments.RemoveAt(Payments.Count - 1);
                Recompute();
            }
        }

        private async Task<bool> CompleteAsync()
        {
            if (Remaining != 0)
            {
                Recompute();
                return true;
            }

            SaleResult? result = null;
            var ok = await RunAsync(async () => result = await sales.CompleteSaleAsync(Cart, [.. Payments], login.Current!, _session!));
            if (!ok)
            {
                return false;
            }

            LastSale = result;
            var change = result!.Ticket.Payments.Sum(p => p.Change ?? 0m);
            Message = $"Ticket {result.Ticket.Number} — {Format.Mad(result.Ticket.DinerShare)}" + (change > 0 ? $" — rendu {Format.Mad(change)}" : "")
                      + (result.AccountDebitedOffline ? " (compte débité hors ligne)" : "");
            if (result.PrintError is not null)
            {
                Error = result.PrintError;
            }

            NewCart();
            return true;
        }

        private void Recompute()
        {
            Total = Cart.Total;
            Subsidy = _session is null ? 0m : Cart.ComputeSubsidy(_session.BusinessDate).EmployerShare;
            OnPropertyChanged(nameof(Due));
            OnPropertyChanged(nameof(Remaining));
            OnPropertyChanged(nameof(DinerLine));
            OnPropertyChanged(nameof(CanPayWithAccount));
            _ = display.ShowAsync(new CustomerDisplayState([.. Cart.Lines.Select(l => new CustomerDisplayLine(l.Label, l.Quantity, l.Amount))],
                Total, Subsidy, Due, Cart.Diner?.DisplayName));
        }
    }

    public sealed partial class HistoryViewModel(AccountOperationsService operations, CashSessionService sessions, OperatorLoginService login)
        : PageViewModel
    {
        [ObservableProperty]
        private IReadOnlyList<LocalTicket> _tickets = [];

        [ObservableProperty]
        private LocalTicket? _selected;

        [ObservableProperty]
        private string _creditReason = "";

        public override string Title => "Historique de la session";

        public bool IsSupervisor => login.Current?.IsSupervisor == true;

        public async Task LoadAsync() => await RunAsync(async () =>
        {
            var session = await sessions.GetOpenSessionAsync();
            Tickets = session is null ? [] : await operations.ListSessionTicketsAsync(session.Id);
        });

        [RelayCommand]
        private async Task ReprintAsync()
        {
            if (Selected is { } t)
            {
                await RunAsync(async () => Error = await operations.ReprintAsync(t.Id, login.Current!.DisplayName));
            }
        }

        [RelayCommand]
        private async Task CreditNoteAsync()
        {
            if (Selected is not { } t)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(CreditReason))
            {
                Error = "Le motif de l'avoir est obligatoire.";
                return;
            }

            var session = await sessions.GetOpenSessionAsync();
            if (await RunAsync(async () =>
                {
                    var result = await operations.IssueCreditNoteAsync(t.Id, CreditReason, login.Current!, session!);
                    Message = $"Avoir {result.Ticket.Number} émis ({Format.Mad(result.Ticket.TotalAmount)}).";
                }))
            {
                CreditReason = string.Empty;
                await LoadAsync();
            }
        }
    }

    public sealed partial class TopUpViewModel(AccountOperationsService operations, SaleService sales, CashSessionService sessions,
        OperatorLoginService login) : PageViewModel, IBadgeAware
    {
        [ObservableProperty]
        private DinerContext? _diner;

        [ObservableProperty]
        private decimal _amount;

        [ObservableProperty]
        private string _method = nameof(PaymentMethod.Cash);

        public override string Title => "Recharge / solde";

        public Task LoadAsync() => Task.CompletedTask;

        public void OnBadgeScanned(string number) => _ = ScanAsync(number);

        [RelayCommand]
        public async Task ScanAsync(string number) => await RunAsync(async () =>
        {
            var session = await sessions.GetOpenSessionAsync();
            Diner = await sales.IdentifyBadgeAsync(number, session?.BusinessDate ?? sessions.Today());
            Message = Diner.CanPayWithAccount
                ? $"{Diner.DisplayName} — solde disponible {Format.Mad(Diner.Available)}{(Diner.IsOnline ? "" : " (hors ligne)")}"
                : $"{Diner.DisplayName} — aucun compte utilisable ici";
        });

        [RelayCommand]
        private async Task TopUpAsync()
        {
            if (Diner is null)
            {
                Error = "Badger d'abord le convive.";
                return;
            }

            var session = await sessions.GetOpenSessionAsync();
            await RunAsync(async () =>
            {
                var result = await operations.TopUpAsync(Diner, Amount, Enum.Parse<PaymentMethod>(Method), login.Current!, session!);
                Message = $"Recharge de {Format.Mad(result.Amount)} enregistrée"
                          + (result.BalanceAfter is { } b ? $" — nouveau solde {Format.Mad(b)}" : " (hors ligne, sera synchronisée)");
                Error = result.PrintError;
                Amount = 0;
            });
        }
    }
}
