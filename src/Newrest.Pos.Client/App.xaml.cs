using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Client.Core;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Client.Infrastructure;
using Newrest.Pos.Client.Views;
using Newrest.Pos.Devices.Badges;
using Newrest.Pos.Devices.Display;
using Newrest.Pos.Devices.Printing;
using Serilog;

namespace Newrest.Pos.Client;

/// <summary>Composition root of the register: configuration, devices, services, windows.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // French UI; Arabic (RTL) will switch FlowDirection at window level.
        var culture = new CultureInfo("fr-MA");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        base.OnStartup(e);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .Build();
        var options = configuration.GetSection("Register").Get<RegisterOptions>() ?? new RegisterOptions();
        var devices = configuration.GetSection("Devices").Get<DeviceOptions>() ?? new DeviceOptions();
        Directory.CreateDirectory(options.DataFolder);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(options.DataFolder, "logs", "caisse-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddRegisterCore(options);
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IDeviceKeyStore>(new DpapiDeviceKeyStore(options.DataFolder));
        services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        AddDevices(services, devices, options);
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            MessageBox.Show("Erreur inattendue. L'opération en cours n'a pas été enregistrée si aucun ticket n'a été imprimé.\n" + args.Exception.Message,
                "Newrest POS", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
        if (_services.GetRequiredService<ICustomerDisplay>() is WindowCustomerDisplay display && devices.CustomerDisplay == "Window")
        {
            CustomerDisplayWindow.ShowOnSecondaryScreen(display);
        }

        await _services.GetRequiredService<ShellViewModel>().StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void AddDevices(IServiceCollection services, DeviceOptions devices, RegisterOptions options)
    {
        var encoder = new EscPosEncoder();
        IReceiptPrinter printer = devices.Printer switch
        {
            "EscPosTcp" => new EscPosReceiptPrinter(Tcp(devices.PrinterTarget), encoder, devices.PrinterLineWidth),
            "EscPosSpooler" => new EscPosReceiptPrinter(new WindowsSpoolerTransport(devices.PrinterTarget), encoder, devices.PrinterLineWidth),
            "EscPosFile" => new EscPosReceiptPrinter(new FilePrinterTransport(devices.PrinterTarget), encoder, devices.PrinterLineWidth),
            _ => new SimulatedReceiptPrinter(Path.Combine(options.DataFolder, "receipts"), devices.PrinterLineWidth),
        };
        services.AddSingleton(printer);
        services.AddSingleton((ICashDrawer)printer);

        switch (devices.BadgeReader)
        {
            case "Serial":
                var serial = new SerialBadgeReader(devices.BadgeReaderPort);
                serial.Open();
                services.AddSingleton<IBadgeReader>(serial);
                break;
            case "Simulated":
                services.AddSingleton<IBadgeReader, SimulatedBadgeReader>();
                break;
            default:
                services.AddSingleton<KeyboardWedgeBadgeReader>(sp => new KeyboardWedgeBadgeReader(TimeProvider.System));
                services.AddSingleton<IBadgeReader>(sp => sp.GetRequiredService<KeyboardWedgeBadgeReader>());
                break;
        }

        if (devices.CustomerDisplay == "Window")
        {
            services.AddSingleton<WindowCustomerDisplay>();
            services.AddSingleton<ICustomerDisplay>(sp => sp.GetRequiredService<WindowCustomerDisplay>());
        }
        else
        {
            services.AddSingleton<ICustomerDisplay, SimulatedCustomerDisplay>();
        }
    }

    private static TcpPrinterTransport Tcp(string target)
    {
        var parts = target.Split(':');
        return new TcpPrinterTransport(parts[0], parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 9100);
    }
}
