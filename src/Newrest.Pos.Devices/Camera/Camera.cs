namespace Newrest.Pos.Devices.Camera;

/// <summary>Region of the frame kept for recognition: the tray only (never faces).</summary>
public sealed record CropRegion(int X, int Y, int Width, int Height);

/// <summary>Fixed exposure and white balance give stable images for the models (configured per register).</summary>
public sealed record CameraOptions(int DeviceIndex = 0, int Width = 1920, int Height = 1080, double? Exposure = -6, int? WhiteBalanceKelvin = 4500,
    CropRegion? Crop = null, int JpegQuality = 90);

public sealed record CapturedImage(byte[] Jpeg, int Width, int Height, DateTimeOffset CapturedAt);

/// <summary>Tray camera. Implementations (OpenCV / Media Foundation) are delivered with the vision service (phase 4).</summary>
public interface ICamera : IDevice
{
    Task<CapturedImage> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>Returns images from a folder in turn (tests, demos, training data replay).</summary>
public sealed class SimulatedCamera(string? folder = null, TimeProvider? clock = null) : ICamera
{
    private int _index;

    public string Name => "Caméra simulée";

    public DeviceStatus Status => DeviceStatus.Ready;

    public async Task<CapturedImage> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var files = folder is not null && Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.jpg").Order(StringComparer.Ordinal).ToArray()
            : [];
        if (files.Length == 0)
        {
            return new CapturedImage([0xFF, 0xD8, 0xFF, 0xD9], 1, 1, now);
        }

        var file = files[Interlocked.Increment(ref _index) % files.Length];
        return new CapturedImage(await File.ReadAllBytesAsync(file, cancellationToken), 0, 0, now);
    }
}
