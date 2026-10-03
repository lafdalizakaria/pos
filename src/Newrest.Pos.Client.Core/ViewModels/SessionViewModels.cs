using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Client.Core.ViewModels;

public sealed partial class SetupViewModel(RegisterSetupService setup, Lazy<ShellViewModel> shell) : PageViewModel
{
    [ObservableProperty]
    private string _serverUrl = "https://";

    [ObservableProperty]
    private string _registerId = "";

    [ObservableProperty]
    private string _deviceKey = "";

    public override string Title => "Enregistrement de la caisse";

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var url) || !Guid.TryParse(RegisterId, out var id) || string.IsNullOrWhiteSpace(DeviceKey))
        {
            Error = "Adresse du serveur, identifiant de caisse et clé sont obligatoires.";
            return;
        }

        if (await RunAsync(() => setup.RegisterAsync(url, id, DeviceKey)))
        {
            DeviceKey = string.Empty;
            await shell.Value.AfterRegistrationAsync();
        }
    }
}

public sealed partial class LoginViewModel(OperatorLoginService login, Lazy<ShellViewModel> shell) : PageViewModel
{
    [ObservableProperty]
    private string _operatorCode = "";

    [ObservableProperty]
    private string _pin = "";

    public override string Title => "Connexion";

    [RelayCommand]
    private void PressDigit(string digit)
    {
        if (Pin.Length < 8)
        {
            Pin += digit;
        }
    }

    [RelayCommand]
    private void ClearPin() => Pin = string.Empty;

    [RelayCommand]
    private async Task LoginAsync()
    {
        LoginOutcome outcome = LoginOutcome.InvalidPin;
        await RunAsync(async () => outcome = await login.LoginAsync(OperatorCode, Pin));
        Pin = string.Empty;
        Error = outcome switch
        {
            LoginOutcome.Success => null,
            LoginOutcome.UnknownOperator => "Opérateur inconnu.",
            LoginOutcome.LockedOut => "Trop d'essais : opérateur verrouillé pendant 15 minutes.",
            LoginOutcome.Inactive => "Opérateur désactivé.",
            _ => "PIN incorrect.",
        };
        if (outcome == LoginOutcome.Success)
        {
            await shell.Value.AfterLoginAsync();
        }
    }
}

public sealed partial class OpenSessionViewModel(CashSessionService sessions, OperatorLoginService login, RegisterOptions options,
    Lazy<ShellViewModel> shell) : PageViewModel
{
    [ObservableProperty]
    private decimal _openingFloat = options.DefaultOpeningFloat;

    public override string Title => "Ouverture de caisse";

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await RunAsync(() => sessions.OpenAsync(login.Current!.Id, OpeningFloat)))
        {
            await shell.Value.GoToSaleAsync();
        }
    }
}

public sealed partial class CloseSessionViewModel(CashSessionService sessions, OperatorLoginService login, Lazy<ShellViewModel> shell) : PageViewModel
{
    [ObservableProperty]
    private decimal _countedCash;

    [ObservableProperty]
    private ZReport? _preview;

    [ObservableProperty]
    private ZReport? _closed;

    public override string Title => "Clôture (Z)";

    public bool IsSupervisor => login.Current?.IsSupervisor == true;

    public Task LoadAsync() => Task.CompletedTask;

    /// <summary>Blind count first, then the expected amount is shown (limits the temptation to adjust the count).</summary>
    [RelayCommand]
    private async Task PreviewAsync() => await RunAsync(async () => Preview = await sessions.PreviewAsync(CountedCash));

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (await RunAsync(async () => Closed = await sessions.CloseAsync(login.Current!.Id, CountedCash, forced: false)))
        {
            Message = $"Clôture Z n° {Closed!.ZNumber} enregistrée. Écart : {Format.Mad(Closed.CashDifference)}.";
        }
    }

    [RelayCommand]
    private void Finish()
    {
        login.Logout();
        shell.Value.Navigate<LoginViewModel>();
    }
}
