using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Newrest.Pos.Devices.Printing;

/// <summary>Raw printing to a USB printer installed in Windows (Generic / Text Only driver), through the spooler.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSpoolerTransport(string printerName) : IPrinterTransport
{
    public string Description => $"spooler://{printerName}";

    public Task SendAsync(byte[] data, CancellationToken cancellationToken = default) => Task.Run(() => Send(data), cancellationToken);

    private void Send(byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Printer '{printerName}' not found.");
        }

        try
        {
            var info = new DocInfo { DocName = "Newrest POS ticket", DataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref info) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                StartPagePrinter(handle);
                var unmanaged = Marshal.AllocCoTaskMem(data.Length);
                try
                {
                    Marshal.Copy(data, 0, unmanaged, data.Length);
                    if (!WritePrinter(handle, unmanaged, data.Length, out var written) || written != data.Length)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(unmanaged);
                }

                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);
}
