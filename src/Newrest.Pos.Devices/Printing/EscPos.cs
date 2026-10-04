using System.Text;

namespace Newrest.Pos.Devices.Printing;

/// <summary>
/// ESC/POS encoder (Epson TM, Star in ESC/POS mode, most Chinese thermal printers). French accents are printed with code
/// page WPC1252 (ESC t 16); configure <see cref="CodePage"/> otherwise. Arabic needs raster printing (later, with RTL).
/// </summary>
public sealed class EscPosEncoder
{
    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;
    private readonly Encoding _encoding;

    static EscPosEncoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public EscPosEncoder(int codePage = 1252, byte escPosCodeTable = 16)
    {
        _encoding = Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        CodePage = codePage;
        CodeTable = escPosCodeTable;
    }

    public int CodePage { get; }

    public byte CodeTable { get; }

    public byte[] Encode(ReceiptDocument receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var buffer = new MemoryStream();
        buffer.Write([Esc, (byte)'@']);              // initialise
        buffer.Write([Esc, (byte)'t', CodeTable]);   // character code table
        foreach (var line in receipt.Lines)
        {
            buffer.Write([Esc, (byte)'a', line.Alignment switch { ReceiptAlignment.Center => (byte)1, ReceiptAlignment.Right => (byte)2, _ => (byte)0 }]);
            buffer.Write([Esc, (byte)'E', line.Bold ? (byte)1 : (byte)0]);
            buffer.Write([Gs, (byte)'!', line.Large ? (byte)0x11 : (byte)0x00]);
            buffer.Write(_encoding.GetBytes(line.Text));
            buffer.WriteByte((byte)'\n');
        }

        buffer.Write([Gs, (byte)'!', 0, Esc, (byte)'E', 0, Esc, (byte)'a', 0]);
        if (receipt.Cut)
        {
            buffer.Write([Esc, (byte)'d', 4]);            // feed 4 lines
            buffer.Write([Gs, (byte)'V', 66, 0]);         // partial cut
        }

        if (receipt.OpenDrawer)
        {
            buffer.Write(DrawerPulse);
        }

        return buffer.ToArray();
    }

    /// <summary>ESC p 0 25 250: pulse on drawer pin 2 (50 ms on, 500 ms off).</summary>
    public static byte[] DrawerPulse => [Esc, (byte)'p', 0, 25, 250];
}

/// <summary>Where ESC/POS bytes are sent.</summary>
public interface IPrinterTransport
{
    string Description { get; }

    Task SendAsync(byte[] data, CancellationToken cancellationToken = default);
}

/// <summary>Network printer (raw TCP, port 9100).</summary>
public sealed class TcpPrinterTransport(string host, int port = 9100, int timeoutMs = 5000) : IPrinterTransport
{
    public string Description => $"tcp://{host}:{port}";

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(data, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }
}

/// <summary>
/// Device or file path: a Windows shared printer (<c>\\localhost\TicketPrinter</c>), <c>/dev/usb/lp0</c> on Linux,
/// or a plain file for diagnostics.
/// </summary>
public sealed class FilePrinterTransport(string path, bool append = false) : IPrinterTransport
{
    public string Description => path;

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, append ? FileMode.Append : FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        await stream.WriteAsync(data, cancellationToken);
    }
}

public sealed class EscPosReceiptPrinter(IPrinterTransport transport, EscPosEncoder encoder, int lineWidth = 42) : IReceiptPrinter, ICashDrawer
{
    public string Name => $"ESC/POS {transport.Description}";

    public DeviceStatus Status { get; private set; } = DeviceStatus.Unknown;

    public int LineWidth => lineWidth;

    public async Task PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default) =>
        await SendAsync(encoder.Encode(receipt), cancellationToken);

    public Task OpenAsync(CancellationToken cancellationToken = default) => SendAsync(EscPosEncoder.DrawerPulse, cancellationToken);

    private async Task SendAsync(byte[] data, CancellationToken cancellationToken)
    {
        Status = DeviceStatus.Busy;
        try
        {
            await transport.SendAsync(data, cancellationToken);
            Status = DeviceStatus.Ready;
        }
        catch
        {
            Status = DeviceStatus.Error;
            throw;
        }
    }
}
