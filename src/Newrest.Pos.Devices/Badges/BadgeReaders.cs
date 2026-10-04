using System.IO.Ports;
using System.Text;

namespace Newrest.Pos.Devices.Badges;

public sealed class BadgeScannedEventArgs(string number) : EventArgs
{
    public string Number { get; } = number;
}

public interface IBadgeReader : IDevice
{
    event EventHandler<BadgeScannedEventArgs>? BadgeScanned;
}

/// <summary>
/// Detects a badge typed by a HID reader in keyboard mode: characters arriving faster than a human can type,
/// terminated by Enter. Slow typing (the cashier) is ignored and handed back to the UI.
/// </summary>
public sealed class KeyboardWedgeDetector(TimeSpan? maxInterKeyDelay = null, int minLength = 4, int maxLength = 64)
{
    private readonly StringBuilder _buffer = new();
    private readonly TimeSpan _maxDelay = maxInterKeyDelay ?? TimeSpan.FromMilliseconds(60);
    private DateTimeOffset _lastKey = DateTimeOffset.MinValue;

    /// <summary>Feeds a character. Returns the badge number when Enter completes a fast burst, otherwise null.</summary>
    public string? OnCharacter(char c, DateTimeOffset at)
    {
        if (_buffer.Length > 0 && at - _lastKey > _maxDelay)
        {
            _buffer.Clear();
        }

        _lastKey = at;
        if (c is '\r' or '\n')
        {
            var value = _buffer.ToString();
            _buffer.Clear();
            return value.Length >= minLength ? value : null;
        }

        if (char.IsControl(c))
        {
            return null;
        }

        if (_buffer.Length >= maxLength)
        {
            _buffer.Clear();
        }

        _buffer.Append(c);
        return null;
    }

    public void Reset() => _buffer.Clear();
}

/// <summary>HID keyboard-wedge reader: the UI forwards text input to <see cref="OnCharacter"/>.</summary>
public sealed class KeyboardWedgeBadgeReader(TimeProvider clock, KeyboardWedgeDetector? detector = null) : IBadgeReader
{
    private readonly KeyboardWedgeDetector _detector = detector ?? new KeyboardWedgeDetector();

    public event EventHandler<BadgeScannedEventArgs>? BadgeScanned;

    public string Name => "Lecteur de badge (clavier)";

    public DeviceStatus Status => DeviceStatus.Ready;

    /// <returns>True when the character completed a badge (the UI should then swallow the key).</returns>
    public bool OnCharacter(char c)
    {
        if (_detector.OnCharacter(c, clock.GetUtcNow()) is not { } number)
        {
            return false;
        }

        BadgeScanned?.Invoke(this, new BadgeScannedEventArgs(number));
        return true;
    }
}

/// <summary>Serial (COM / USB-serial) reader sending one badge per line.</summary>
public sealed class SerialBadgeReader : IBadgeReader, IDisposable
{
    private readonly SerialPort _port;

    public SerialBadgeReader(string portName, int baudRate = 9600)
    {
        _port = new SerialPort(portName, baudRate) { NewLine = "\r", ReadTimeout = SerialPort.InfiniteTimeout };
        _port.DataReceived += (_, _) =>
        {
            try
            {
                var line = _port.ReadLine().Trim();
                if (line.Length > 0)
                {
                    BadgeScanned?.Invoke(this, new BadgeScannedEventArgs(line));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                Status = DeviceStatus.Error;
            }
        };
    }

    public event EventHandler<BadgeScannedEventArgs>? BadgeScanned;

    public string Name => $"Lecteur série {_port.PortName}";

    public DeviceStatus Status { get; private set; } = DeviceStatus.Unknown;

    public void Open()
    {
        _port.Open();
        Status = DeviceStatus.Ready;
    }

    public void Dispose() => _port.Dispose();
}

public sealed class SimulatedBadgeReader : IBadgeReader
{
    public event EventHandler<BadgeScannedEventArgs>? BadgeScanned;

    public string Name => "Lecteur de badge simulé";

    public DeviceStatus Status => DeviceStatus.Ready;

    public void Scan(string number) => BadgeScanned?.Invoke(this, new BadgeScannedEventArgs(number));
}
