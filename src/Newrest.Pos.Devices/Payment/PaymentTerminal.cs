namespace Newrest.Pos.Devices.Payment;

public enum TerminalOutcome
{
    /// <summary>Standalone terminal: the cashier types the amount on the terminal and the authorisation reference at the register.</summary>
    ManualEntryRequired,
    Approved,
    Declined,
    Unavailable,
}

public sealed record TerminalResult(TerminalOutcome Outcome, string? AuthorizationCode = null, string? Message = null);

/// <summary>Card terminal. Today: standalone (manual); the interface is ready for an integrated CMI terminal.</summary>
public interface IPaymentTerminal : IDevice
{
    Task<TerminalResult> RequestPaymentAsync(decimal amount, string reference, CancellationToken cancellationToken = default);
}

public sealed class ManualPaymentTerminal : IPaymentTerminal
{
    public string Name => "TPE autonome";

    public DeviceStatus Status => DeviceStatus.Ready;

    public Task<TerminalResult> RequestPaymentAsync(decimal amount, string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TerminalResult(TerminalOutcome.ManualEntryRequired,
            Message: $"Saisir {amount:0.00} MAD sur le TPE puis la référence d'autorisation."));
}

public sealed class SimulatedPaymentTerminal(bool approve = true) : IPaymentTerminal
{
    private int _counter;

    public string Name => "TPE simulé";

    public DeviceStatus Status => DeviceStatus.Ready;

    public Task<TerminalResult> RequestPaymentAsync(decimal amount, string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(approve
            ? new TerminalResult(TerminalOutcome.Approved, $"SIM{Interlocked.Increment(ref _counter):D6}")
            : new TerminalResult(TerminalOutcome.Declined, Message: "Refusé"));
}
