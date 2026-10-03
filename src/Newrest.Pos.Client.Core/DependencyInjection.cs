using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Receipts;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Core.ViewModels;

namespace Newrest.Pos.Client.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Register services (singletons: one register = one process). Devices (<c>IReceiptPrinter</c>, <c>ICashDrawer</c>,
    /// <c>ICustomerDisplay</c>, <c>IBadgeReader</c>), <see cref="IDeviceKeyStore"/>, <see cref="IUiDispatcher"/> and the
    /// <see cref="HttpClient"/> are registered by the host.
    /// </summary>
    public static IServiceCollection AddRegisterCore(this IServiceCollection services, RegisterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new LocalStore(Path.Combine(options.DataFolder, "register.db")));
        services.AddSingleton<ConnectivityState>();
        services.AddSingleton<PosApiClient>();
        services.AddSingleton<ReferenceCache>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<RegisterSetupService>();
        services.AddSingleton<OperatorLoginService>();
        services.AddSingleton<ReceiptBuilder>();
        services.AddSingleton<CashSessionService>();
        services.AddSingleton<SaleService>();
        services.AddSingleton<AccountOperationsService>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton(sp => new Lazy<ShellViewModel>(sp.GetRequiredService<ShellViewModel>));
        services.AddTransient<SetupViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<OpenSessionViewModel>();
        services.AddTransient<SaleViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<TopUpViewModel>();
        services.AddTransient<CloseSessionViewModel>();
        return services;
    }
}
