using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newrest.Pos.Client.Core.Data;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Core.Vision;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Devices.Display;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Sales;
using Newrest.Pos.Domain.Vision;

namespace Newrest.Pos.Client.Core.Data
{
    public sealed record MenuButton(DailyMenuItemDto Item, string Label, string Price, decimal VatRate, bool IsSubsidizable, string? Color);

    public sealed record MenuCategoryGroup(string Name, string? Color, IReadOnlyList<MenuButton> Items);

    /// <summary>Low-confidence prediction: only the category is suggested, the cashier picks the article from the menu.</summary>
    public sealed record VisionHint(int Index, string CategoryName, MenuButton Predicted, decimal Confidence)
    {
        public string Text => $"{CategoryName} : choisir dans le menu (peut-être {Predicted.Label}, {Confidence * 100:0} %)";
    }
}

namespace Newrest.Pos.Client.Core.ViewModels
{
    /// <summary>
    /// Sale screen: menu of the day by category (+ search), cart, badge, totals, then payment. A standard tray takes
    /// three gestures: tray photo (or items), badge, "Compte". Recognition: confidence ≥ high threshold → line added;
    /// between thresholds → line added highlighted with the second choice one tap away; below → category suggested only.
    /// </summary>
    public sealed partial class SaleViewModel(
        SaleService sales, ReferenceCache cache, CashSessionService sessions, OperatorLoginService login, ICustomerDisplay display, TimeProvider clock,
        TrayRecognitionService vision)
        : PageViewModel, IBadgeAware
    {
        private IReadOnlyList<MenuCategoryGroup> _allGroups = [];
        private Dictionary<Guid, string> _categoryOfArticle = [];
        private LocalCashSession? _session;
        private TrayRecognition? _recognition;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CaptureTrayCommand))]
        private bool _isRecognizing;

        /// <summary>Low-confidence predictions waiting for the cashier's choice.</summary>
        public ObservableCollection<VisionHint> Hints { get; } = [];

        public bool VisionEnabled => vision.IsEnabled;

        /// <summary>Recording of the last recognition outcome (outbox + feedback), exposed for tests.</summary>
        public Task LastOutcome { get; private set; } = Task.CompletedTask;

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
                _categoryOfArticle = _allGroups.SelectMany(g => g.Items.Select(i => (i.Item.ArticleId, g.Name)))
                    .GroupBy(x => x.ArticleId).ToDictionary(x => x.Key, x => x.First().Name);
                ApplySearch();
                _ = Safe(vision.PrefetchPhotosAsync([.. _allGroups.SelectMany(g => g.Items)]));
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
            // A low-confidence prediction of the same category is answered by this choice (its box becomes a training label).
            var hint = Hints.FirstOrDefault(h => h.CategoryName == _categoryOfArticle.GetValueOrDefault(button.Item.ArticleId));
            if (hint is null)
            {
                Cart.Add(button.Item, button.VatRate, button.IsSubsidizable);
            }
            else
            {
                var source = hint.Predicted.Item.ArticleId == button.Item.ArticleId ? LineSource.VisionConfirmed : LineSource.VisionCorrected;
                Cart.Add(button.Item, button.VatRate, button.IsSubsidizable, source).Predictions.Add((hint.Index, source));
                Hints.Remove(hint);
            }

            Recompute();
        }

        /// <summary>Photo of the tray → recognition (6 s maximum). Any failure leaves the cashier with the manual entry.</summary>
        [RelayCommand(CanExecute = nameof(CanCapture))]
        private async Task CaptureTrayAsync()
        {
            IsRecognizing = true;
            Error = null;
            Message = "Reconnaissance du plateau…";
            try
            {
                ClearVisionLines();
                var recognition = await vision.RecognizeAsync(_allGroups);
                RecordOutcome(null);
                _recognition = recognition;
                if (recognition.Failed)
                {
                    Message = null;
                    Error = recognition.Failure;
                    return;
                }

                foreach (var p in recognition.Proposals)
                {
                    switch (p.Decision)
                    {
                        case RecognitionDecision.AutoAccept:
                            Cart.Add(p.Button.Item, p.Button.VatRate, p.Button.IsSubsidizable, LineSource.VisionAuto).Predictions
                                .Add((p.Index, LineSource.VisionAuto));
                            break;
                        case RecognitionDecision.NeedsConfirmation:
                            var line = Cart.Add(p.Button.Item, p.Button.VatRate, p.Button.IsSubsidizable, LineSource.VisionConfirmed, separate: true);
                            line.Alternative = p.Alternative;
                            line.Confidence = p.Confidence;
                            line.NeedsReview = true;
                            line.Predictions.Add((p.Index, LineSource.VisionConfirmed));
                            break;
                        default:
                            Hints.Add(new VisionHint(p.Index, p.CategoryName, p.Button, p.Confidence));
                            break;
                    }
                }

                var review = Cart.Lines.Count(l => l.NeedsReview);
                Message = recognition.Proposals.Count == 0
                    ? "Aucun article reconnu : saisir le plateau."
                    : $"Plateau reconnu en {Format.Seconds(recognition.ElapsedMs)}"
                      + (review > 0 ? $" — {review} ligne(s) à vérifier" : "")
                      + (Hints.Count > 0 ? $" — {Hints.Count} article(s) à choisir" : "");
            }
            finally
            {
                IsRecognizing = false;
                Recompute();
            }
        }

        private bool CanCapture() => vision.IsEnabled && !IsRecognizing;

        /// <summary>The highlighted line is right.</summary>
        [RelayCommand]
        private static void ConfirmLine(CartLine line) => line.NeedsReview = false;

        /// <summary>One tap: the highlighted line becomes the second choice proposed by the model.</summary>
        [RelayCommand]
        private void SwitchToAlternative(CartLine line)
        {
            if (line.Alternative is not { } alternative)
            {
                return;
            }

            var predictions = line.Predictions.Select(p => (p.Index, LineSource.VisionCorrected)).ToList();
            var quantity = line.Quantity;
            Cart.Lines.Remove(line);
            var replacement = Cart.Add(alternative.Item, alternative.VatRate, alternative.IsSubsidizable, LineSource.VisionCorrected);
            replacement.Quantity += quantity - 1;
            replacement.Predictions.AddRange(predictions);
            Recompute();
        }

        [RelayCommand]
        private void DismissHint(VisionHint hint) => Hints.Remove(hint);

        private void ClearVisionLines()
        {
            foreach (var line in Cart.Lines.Where(l => l.Predictions.Count > 0).ToList())
            {
                Cart.Lines.Remove(line);
            }

            Hints.Clear();
        }

        /// <summary>Sends the outcome of the current recognition (sold with <paramref name="ticketId"/>, or abandoned).</summary>
        private void RecordOutcome(Guid? ticketId)
        {
            if (_recognition is { } recognition)
            {
                _recognition = null;
                LastOutcome = Safe(vision.RecordOutcomeAsync(recognition, ticketId, [.. Cart.Lines]));
            }
        }

        private static async Task Safe(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Statistics and photo cache only: never disturb the sale.
                System.Diagnostics.Trace.TraceWarning("Vision side task failed: " + ex.Message);
            }
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
            RecordOutcome(null);
            Hints.Clear();
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
            RecordOutcome(result!.Ticket.Id);
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
