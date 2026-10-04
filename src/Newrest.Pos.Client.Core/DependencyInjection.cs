using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Receipts;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Client.Core.Vision;
using Newrest.Pos.Devices.Camera;

namespace Newrest.Pos.Client.Core;

public static class DependencyInjection
{
    private const string VisionHttp = "vision";

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
        services.AddSingleton(options.Vision);
        // Own HttpClient: the local vision service is not the central API (no token, loopback only).
        services.AddKeyedSingleton(VisionHttp, (_, _) => new HttpClient { Timeout = options.Vision.Timeout + TimeSpan.FromSeconds(2) });
        services.AddSingleton<IVisionClient>(sp => new VisionClient(sp.GetRequiredKeyedService<HttpClient>(VisionHttp), options.Vision));
        services.TryAddSingleton<ICamera>(sp => new VisionServiceCamera(sp.GetRequiredKeyedService<HttpClient>(VisionHttp), options.Vision,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ReferencePhotoCache>();
        services.AddSingleton<TrayRecognitionService>();
        services.AddSingleton<VisionDeploymentService>();
        services.AddSingleton<LocalBackupService>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton(sp => new Lazy<ShellViewModel>(sp.GetRequiredService<ShellViewModel>));
        services.AddTransient<SetupViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<OpenSessionViewModel>();
        services.AddTransient<SaleViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<TopUpViewModel>();
        services.AddTransient<CloseSessionViewModel>();
        services.AddTransient<SupervisorViewModel>();
        return services;
    }
}
