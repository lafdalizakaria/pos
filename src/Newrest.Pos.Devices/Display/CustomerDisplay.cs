namespace Newrest.Pos.Devices.Display;

public sealed record CustomerDisplayLine(string Label, int Quantity, decimal Amount);

/// <summary>What the diner sees: current lines, total, subsidy and amount due, or a message.</summary>
public sealed record CustomerDisplayState(IReadOnlyList<CustomerDisplayLine> Lines, decimal Total, decimal Subsidy, decimal Due, string? Message)
{
    public static readonly CustomerDisplayState Welcome = new([], 0m, 0m, 0m, "Bienvenue — Bon appétit !");
}

public interface ICustomerDisplay : IDevice
{
    Task ShowAsync(CustomerDisplayState state, CancellationToken cancellationToken = default);
}

public sealed class SimulatedCustomerDisplay : ICustomerDisplay
{
    public string Name => "Afficheur client simulé";

    public DeviceStatus Status => DeviceStatus.Ready;

    public CustomerDisplayState Current { get; private set; } = CustomerDisplayState.Welcome;

    public Task ShowAsync(CustomerDisplayState state, CancellationToken cancellationToken = default)
    {
        Current = state ?? throw new ArgumentNullException(nameof(state));
        return Task.CompletedTask;
    }
}
