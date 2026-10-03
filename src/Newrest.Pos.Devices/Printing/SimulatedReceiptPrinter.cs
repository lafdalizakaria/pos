namespace Newrest.Pos.Devices.Printing;

/// <summary>Development / test printer: keeps receipts in memory and optionally writes them as text files.</summary>
public sealed class SimulatedReceiptPrinter(string? outputFolder = null, int lineWidth = 42) : IReceiptPrinter, ICashDrawer
{
    private readonly List<ReceiptDocument> _printed = [];
    private readonly Lock _lock = new();

    public string Name => "Imprimante simulée";

    public DeviceStatus Status { get; set; } = DeviceStatus.Ready;

    public int LineWidth => lineWidth;

    public int DrawerOpenings { get; private set; }

    public IReadOnlyList<ReceiptDocument> Printed
    {
        get
        {
            lock (_lock)
            {
                return [.. _printed];
            }
        }
    }

    public async Task PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (Status is DeviceStatus.Offline or DeviceStatus.Error)
        {
            throw new IOException("Imprimante hors ligne.");
        }

        lock (_lock)
        {
            _printed.Add(receipt);
            if (receipt.OpenDrawer)
            {
                DrawerOpenings++;
            }
        }

        if (outputFolder is not null)
        {
            Directory.CreateDirectory(outputFolder);
            await File.WriteAllTextAsync(Path.Combine(outputFolder, $"receipt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.txt"),
                receipt.ToPlainText(), cancellationToken);
        }
    }

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            DrawerOpenings++;
        }

        return Task.CompletedTask;
    }
}
