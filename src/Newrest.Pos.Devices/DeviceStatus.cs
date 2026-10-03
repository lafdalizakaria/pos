namespace Newrest.Pos.Devices;

/// <summary>
/// Peripheral abstractions (printer, customer display, badge reader, camera, payment terminal, cash drawer)
/// and their simulators are delivered in phase 3. Every device reports this status to the register UI.
/// </summary>
public enum DeviceStatus
{
    Unknown,
    Ready,
    Busy,
    Offline,
    Error,
}
