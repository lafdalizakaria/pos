using CommunityToolkit.Mvvm.ComponentModel;
using Newrest.Pos.Devices;
using Newrest.Pos.Devices.Display;

namespace Newrest.Pos.Client.Infrastructure;

/// <summary>Customer display rendered by <see cref="Views.CustomerDisplayWindow"/> on the second screen.</summary>
public sealed partial class WindowCustomerDisplay(Core.ViewModels.IUiDispatcher dispatcher) : ObservableObject, ICustomerDisplay
{
    [ObservableProperty]
    private CustomerDisplayState _state = CustomerDisplayState.Welcome;

    public string Name => "Afficheur client (second écran)";

    public DeviceStatus Status => DeviceStatus.Ready;

    public Task ShowAsync(CustomerDisplayState state, CancellationToken cancellationToken = default)
    {
        dispatcher.Post(() => State = state);
        return Task.CompletedTask;
    }
}
