using CommunityToolkit.Mvvm.ComponentModel;

namespace Newrest.Pos.Client.Core.Local;

/// <summary>Permanent indicator: online / offline / items waiting to be sent (status bar).</summary>
public sealed partial class ConnectivityState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _pendingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string? _blockingError;

    [ObservableProperty]
    private DateTimeOffset? _lastSyncAt;

    public string Label => BlockingError is not null
        ? $"Synchronisation bloquée : {BlockingError}"
        : (IsOnline ? "En ligne" : "Hors ligne") + (PendingCount > 0 ? $" — {PendingCount} en attente" : string.Empty);
}
