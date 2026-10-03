namespace Newrest.Pos.Client.Core.Configuration;

/// <summary>Register settings (appsettings.json of the register + values written at registration).</summary>
public sealed class RegisterOptions
{
    /// <summary>Central API, e.g. https://pos.newrest.ma/ (TLS mandatory outside development).</summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>Folder of the SQLite database, receipts copies and logs.</summary>
    public string DataFolder { get; set; } = "data";

    /// <summary>Timeout of every online call: beyond it the sale continues offline.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Maximum unconfirmed (offline) account spending per badge on this register.</summary>
    public decimal OfflineSpendingLimitPerBadge { get; set; } = 60m;

    public TimeSpan OutboxInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReferenceSyncInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Above this many pending items, the status bar turns red (alert also raised server-side in phase 6).</summary>
    public int PendingAlertThreshold { get; set; } = 50;

    public int PinMaxFailedAttempts { get; set; } = 5;

    public TimeSpan PinLockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Opening float proposed by default (MAD).</summary>
    public decimal DefaultOpeningFloat { get; set; } = 500m;
}
