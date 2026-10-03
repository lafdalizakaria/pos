using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Devices.Badges;

namespace Newrest.Pos.Client.Core.ViewModels;

/// <summary>Main window: current page, status bar (online / offline / pending), operator, navigation.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly RegisterSetupService _setup;
    private readonly OperatorLoginService _login;
    private readonly CashSessionService _sessions;
    private readonly SyncService _sync;
    private CancellationTokenSource? _syncLoop;

    [ObservableProperty]
    private PageViewModel? _currentPage;

    public ShellViewModel(IServiceProvider services, RegisterSetupService setup, OperatorLoginService login, CashSessionService sessions,
        SyncService sync, ConnectivityState status, IBadgeReader badgeReader, IUiDispatcher dispatcher)
    {
        _services = services;
        _setup = setup;
        _login = login;
        _sessions = sessions;
        _sync = sync;
        Status = status;
        badgeReader.BadgeScanned += (_, e) => dispatcher.Post(() => (CurrentPage as IBadgeAware)?.OnBadgeScanned(e.Number));
        login.PropertyChanged += (_, _) => OnPropertyChanged(nameof(OperatorName));
    }

    public ConnectivityState Status { get; }

    public string OperatorName => _login.Current?.DisplayName ?? string.Empty;

    public string RegisterName => _setup.Profile is { } p ? $"{p.PointOfSaleName} — {p.Name} ({p.TicketPrefix})" : "Caisse non enregistrée";

    public async Task StartAsync()
    {
        if (!await _setup.LoadAsync())
        {
            Navigate<SetupViewModel>();
            return;
        }

        await AfterRegistrationAsync();
    }

    public async Task AfterRegistrationAsync()
    {
        OnPropertyChanged(nameof(RegisterName));
        await _sync.RunOnceAsync(forceReference: true);
        await _setup.RefreshProfileAsync();
        _syncLoop?.Cancel();
        _syncLoop = new CancellationTokenSource();
        _ = Task.Run(() => _sync.RunLoopAsync(_syncLoop.Token));
        Navigate<LoginViewModel>();
    }

    public async Task AfterLoginAsync()
    {
        if (await _sessions.GetOpenSessionAsync() is null)
        {
            Navigate<OpenSessionViewModel>();
        }
        else
        {
            await GoToSaleAsync();
        }
    }

    public async Task GoToSaleAsync()
    {
        var page = Navigate<SaleViewModel>();
        await page.LoadAsync();
    }

    [RelayCommand]
    private async Task ShowSaleAsync() => await GoToSaleAsync();

    [RelayCommand]
    private async Task ShowHistoryAsync() => await Navigate<HistoryViewModel>().LoadAsync();

    [RelayCommand]
    private async Task ShowTopUpAsync() => await Navigate<TopUpViewModel>().LoadAsync();

    [RelayCommand]
    private async Task ShowCloseAsync() => await Navigate<CloseSessionViewModel>().LoadAsync();

    [RelayCommand]
    private async Task SyncNowAsync() => await _sync.RunOnceAsync(forceReference: true);

    [RelayCommand]
    private void Logout()
    {
        _login.Logout();
        Navigate<LoginViewModel>();
    }

    public T Navigate<T>()
        where T : PageViewModel
    {
        var page = _services.GetRequiredService<T>();
        CurrentPage = page;
        return page;
    }
}

/// <summary>Pages that react to badge scans.</summary>
public interface IBadgeAware
{
    void OnBadgeScanned(string number);
}
