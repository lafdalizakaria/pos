using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.ViewModels;

namespace Newrest.Pos.Client.Infrastructure;

/// <summary>Device key encrypted with DPAPI (machine scope): unreadable if the file is copied to another PC.</summary>
public sealed class DpapiDeviceKeyStore(string folder) : IDeviceKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Newrest.Pos.DeviceKey");
    private readonly string _path = Path.Combine(folder, "device.key");

    public string? Load() => File.Exists(_path)
        ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine))
        : null;

    public void Save(string key)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(_path, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.LocalMachine));
    }
}

public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>Peripheral selection from appsettings.json.</summary>
public sealed class DeviceOptions
{
    /// <summary>Simulated, EscPosTcp (host[:port]), EscPosSpooler (Windows printer name), EscPosFile (path or \\host\share).</summary>
    public string Printer { get; set; } = "Simulated";

    public string PrinterTarget { get; set; } = "";

    public int PrinterLineWidth { get; set; } = 42;

    /// <summary>Keyboard (HID wedge), Serial, Simulated.</summary>
    public string BadgeReader { get; set; } = "Keyboard";

    public string BadgeReaderPort { get; set; } = "COM3";

    /// <summary>Window (second screen), Simulated.</summary>
    public string CustomerDisplay { get; set; } = "Window";
}
