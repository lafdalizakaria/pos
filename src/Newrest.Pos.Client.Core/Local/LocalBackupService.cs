using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Client.Core.Configuration;

namespace Newrest.Pos.Client.Core.Local;

/// <summary>
/// Online copies of the register database (SQLite backup API: consistent even while selling), kept in
/// <c>&lt;DataFolder&gt;/backups</c> with rotation. Taken every day and after each Z. A copy is a safety net for a
/// damaged disk or file: the server already holds every synchronised ticket.
/// </summary>
public sealed partial class LocalBackupService(LocalStore store, RegisterOptions options, TimeProvider clock, ILogger<LocalBackupService> logger)
{
    public const string LastBackupKey = "backup.lastAt";

    public string Folder => Path.Combine(options.DataFolder, "backups");

    public async Task<string> BackupAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        var now = clock.GetUtcNow();
        var path = Path.Combine(Folder, $"register-{now.UtcDateTime:yyyyMMdd-HHmmss-fff}.db");
        await Task.Run(() =>
        {
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            source.Open();
            target.Open();
            source.BackupDatabase(target);
            using var check = target.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            var result = check.ExecuteScalar() as string;
            if (result != "ok")
            {
                throw new InvalidDataException($"Backup integrity check failed: {result}");
            }
        }, ct);

        foreach (var old in Directory.GetFiles(Folder, "register-*.db").Order(StringComparer.Ordinal).SkipLast(options.BackupRetention))
        {
            File.Delete(old);
        }

        await store.SetSettingAsync(LastBackupKey, now.ToString("O", CultureInfo.InvariantCulture), ct);
        LogBackup(logger, path);
        return path;
    }

    public async Task<DateTimeOffset?> LastBackupAtAsync(CancellationToken ct = default) =>
        await store.GetSettingAsync(LastBackupKey, ct) is { } value ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture) : null;

    /// <summary>Backs up when the last copy is older than <see cref="RegisterOptions.BackupInterval"/>. Never throws.</summary>
    public async Task BackupIfDueAsync(CancellationToken ct = default, bool force = false)
    {
        try
        {
            var last = await LastBackupAtAsync(ct);
            if (force || last is null || clock.GetUtcNow() - last >= options.BackupInterval)
            {
                await BackupAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SqliteException or InvalidDataException or UnauthorizedAccessException)
        {
            LogFailed(logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Local database backed up to {Path}")]
    private static partial void LogBackup(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Local backup failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
