using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Data;

namespace Newrest.Pos.Client.Core.ViewModels;

public sealed record PendingItemRow(long Position, string Kind, string Status, int Attempts, string? LastError, DateTimeOffset CreatedAt);

/// <summary>Supervisor screen: synchronisation queue (with retry after a refusal), local backups, versions.</summary>
public sealed partial class SupervisorViewModel(SyncService sync, LocalStore store, LocalBackupService backups, OperatorLoginService login,
    ConnectivityState status) : PageViewModel
{
    [ObservableProperty]
    private IReadOnlyList<PendingItemRow> _items = [];

    [ObservableProperty]
    private string _backupLine = "";

    public override string Title => "Responsable";

    public bool IsSupervisor => login.Current?.IsSupervisor == true;

    public ConnectivityState Status => status;

    public string VersionLine => $"Caisse {SyncService.AppVersion} — {status.VisionLabel ?? "vision : état inconnu"}";

    public async Task LoadAsync() => await RunAsync(async () =>
    {
        await using var db = store.Open();
        Items = [.. (await db.Outbox.AsNoTracking().Where(o => o.Status != OutboxStatus.Sent).OrderBy(o => o.Position).Take(200).ToListAsync())
            .Select(o => new PendingItemRow(o.Position, o.Kind.ToString(), o.Status == OutboxStatus.Rejected ? "Refusé" : "En attente", o.Attempts, o.LastError,
                o.CreatedAt))];
        var last = await backups.LastBackupAtAsync();
        BackupLine = last is { } at ? $"Dernière sauvegarde locale : {at.ToLocalTime():dd/MM/yyyy HH:mm} ({backups.Folder})" : "Aucune sauvegarde locale.";
    });

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        await RunAsync(() => sync.RunOnceAsync(forceReference: true));
        await LoadAsync();
    }

    /// <summary>After the cause of a refusal has been fixed (e.g. register reactivated in the back-office).</summary>
    [RelayCommand]
    private async Task RetryRejectedAsync()
    {
        if (!IsSupervisor)
        {
            Error = "Réservé au responsable.";
            return;
        }

        await RunAsync(async () =>
        {
            await sync.RetryRejectedAsync();
            await sync.RunOnceAsync();
        });
        await LoadAsync();
        Message = status.BlockingError is null ? "File relancée." : $"Toujours bloquée : {status.BlockingError}";
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        await RunAsync(async () => Message = $"Sauvegarde : {await backups.BackupAsync()}");
        await LoadAsync();
    }
}
