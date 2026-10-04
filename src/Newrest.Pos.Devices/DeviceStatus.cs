namespace Newrest.Pos.Devices;

/// <summary>State reported by every peripheral to the register status bar.</summary>
public enum DeviceStatus
{
    Unknown,
    Ready,
    Busy,
    Offline,
    Error,
}

public interface IDevice
{
    string Name { get; }

    DeviceStatus Status { get; }
}
