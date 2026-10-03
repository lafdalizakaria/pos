using CommunityToolkit.Mvvm.ComponentModel;

namespace Newrest.Pos.Client.ViewModels;

/// <summary>Shell of the register application. Screens (PIN, session, sale, payment, Z) are delivered in phase 3.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Newrest - Caisse";

    [ObservableProperty]
    private string _status = "Application caisse : livrée en phase 3";
}
