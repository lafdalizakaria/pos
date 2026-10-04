using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Operations;

/// <summary>Last state reported by a register (one row per register, sent every minute while it runs).</summary>
public sealed class RegisterHeartbeat : Entity
{
    private RegisterHeartbeat()
    {
    }

    public RegisterHeartbeat(Guid registerId) : base(Guard.NotEmpty(registerId, nameof(registerId)))
    {
    }

    public Guid RegisterId => Id;
    public string AppVersion { get; private set; } = "";
    public int PendingCount { get; private set; }
    public DateTimeOffset? OldestPendingAt { get; private set; }
    public string? BlockingError { get; private set; }
    public long LocalLastSequence { get; private set; }
    public DateTimeOffset? OpenSessionSince { get; private set; }
    public DateTimeOffset? LastBackupAt { get; private set; }
    public DateTimeOffset ReportedAt { get; private set; }

    public void Record(string appVersion, int pendingCount, DateTimeOffset? oldestPendingAt, string? blockingError, long localLastSequence,
        DateTimeOffset? openSessionSince, DateTimeOffset? lastBackupAt, DateTimeOffset now)
    {
        AppVersion = Guard.NotBlank(appVersion, nameof(appVersion), 64);
        PendingCount = Math.Max(0, pendingCount);
        OldestPendingAt = oldestPendingAt;
        BlockingError = blockingError is null ? null : blockingError.Length > 300 ? blockingError[..300] : blockingError;
        LocalLastSequence = Math.Max(0, localLastSequence);
        OpenSessionSince = openSessionSince;
        LastBackupAt = lastBackupAt;
        ReportedAt = now;
    }
}

/// <summary>Result of a server-side verification of a register's ticket chain (nightly job or on demand).</summary>
public sealed class IntegrityCheck : Entity
{
    private IntegrityCheck()
    {
    }

    public IntegrityCheck(Guid registerId, DateTimeOffset checkedAt, int ticketsChecked, bool isValid, string issuesSummary)
    {
        RegisterId = Guard.NotEmpty(registerId, nameof(registerId));
        CheckedAt = checkedAt;
        TicketsChecked = ticketsChecked;
        IsValid = isValid;
        IssuesSummary = issuesSummary.Length > 2000 ? issuesSummary[..2000] : issuesSummary;
    }

    public Guid RegisterId { get; private set; }
    public DateTimeOffset CheckedAt { get; private set; }
    public int TicketsChecked { get; private set; }
    public bool IsValid { get; private set; }
    public string IssuesSummary { get; private set; } = "";
}

public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <param name="Key">Stable identity of the alert (deduplicates notifications).</param>
public sealed record Alert(string Key, AlertSeverity Severity, string Code, Guid? RegisterId, string Subject, string Message, DateTimeOffset Since);

/// <summary>Configurable thresholds (section <c>Supervision</c>).</summary>
public sealed record SupervisionThresholds
{
    public TimeSpan SilentAfter { get; init; } = TimeSpan.FromMinutes(30);
    public int BacklogCount { get; init; } = 50;
    public TimeSpan BacklogAge { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan SessionMaxDuration { get; init; } = TimeSpan.FromHours(20);
    public TimeSpan IntegrityMaxAge { get; init; } = TimeSpan.FromHours(36);
    public TimeSpan BackupMaxAge { get; init; } = TimeSpan.FromHours(30);
    public TimeSpan DebitWithoutTicketAfter { get; init; } = TimeSpan.FromHours(2);
}

public sealed record RegisterSnapshot(
    Guid RegisterId, string Prefix, string SiteName, DateTimeOffset? LastSeenAt, RegisterHeartbeat? Heartbeat, DateTimeOffset? OpenSessionSince,
    IntegrityCheck? LatestIntegrity, bool HasTickets, bool? VisionUpToDate, string? VisionError, DateTimeOffset? VisionReportedAt);

/// <param name="DebitsWithoutTicket">Account consumptions whose ticket has not been received (id, register prefix, amount, occurred at).</param>
public sealed record SupervisionSnapshot(IReadOnlyList<RegisterSnapshot> Registers,
    IReadOnlyList<(Guid MovementId, string RegisterPrefix, decimal Amount, DateTimeOffset OccurredAt)> DebitsWithoutTicket,
    int OfflineOverdraftsLast7Days);

/// <summary>Turns the state of the registers and of the ledger into alerts for the operations team.</summary>
public static class SupervisionRules
{
    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public static IReadOnlyList<Alert> Evaluate(SupervisionSnapshot snapshot, SupervisionThresholds thresholds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);
        var alerts = new List<Alert>();
        foreach (var r in snapshot.Registers)
        {
            var subject = $"{r.Prefix} ({r.SiteName})";
            var lastSeen = Max(r.LastSeenAt, r.Heartbeat?.ReportedAt);
            if (r.OpenSessionSince is { } open)
            {
                if (lastSeen is null || now - lastSeen > thresholds.SilentAfter)
                {
                    alerts.Add(new Alert($"silent:{r.RegisterId}", AlertSeverity.Critical, "register_silent", r.RegisterId, subject,
                        $"Caisse ouverte sans nouvelles depuis {Ago(now, lastSeen ?? open)} (réseau, poste éteint ?).", lastSeen ?? open));
                }

                if (now - open > thresholds.SessionMaxDuration)
                {
                    alerts.Add(new Alert($"session:{r.RegisterId}", AlertSeverity.Warning, "session_not_closed", r.RegisterId, subject,
                        $"Session ouverte depuis {Ago(now, open)} : Z non fait.", open));
                }
            }

            if (r.Heartbeat is { } hb)
            {
                if (hb.BlockingError is { } blocked)
                {
                    alerts.Add(new Alert($"blocked:{r.RegisterId}", AlertSeverity.Critical, "queue_blocked", r.RegisterId, subject,
                        $"File de synchronisation bloquée : {blocked}", hb.ReportedAt));
                }
                else if (hb.PendingCount >= thresholds.BacklogCount || (hb.OldestPendingAt is { } oldest && now - oldest > thresholds.BacklogAge))
                {
                    alerts.Add(new Alert($"backlog:{r.RegisterId}", AlertSeverity.Warning, "sync_backlog", r.RegisterId, subject,
                        $"{hb.PendingCount} élément(s) en attente d'envoi" + (hb.OldestPendingAt is { } o ? $", le plus ancien depuis {Ago(now, o)}." : "."),
                        hb.OldestPendingAt ?? hb.ReportedAt));
                }

                if (r.OpenSessionSince is not null && (hb.LastBackupAt is null || now - hb.LastBackupAt > thresholds.BackupMaxAge))
                {
                    alerts.Add(new Alert($"backup:{r.RegisterId}", AlertSeverity.Warning, "backup_missing", r.RegisterId, subject,
                        hb.LastBackupAt is { } b ? $"Dernière sauvegarde locale il y a {Ago(now, b)}." : "Aucune sauvegarde locale signalée.",
                        hb.LastBackupAt ?? hb.ReportedAt));
                }
            }

            if (r.LatestIntegrity is { IsValid: false } bad)
            {
                alerts.Add(new Alert($"integrity:{r.RegisterId}", AlertSeverity.Critical, "integrity_failed", r.RegisterId, subject,
                    $"Chaîne d'intégrité invalide : {bad.IssuesSummary}", bad.CheckedAt));
            }
            else if (r.HasTickets && (r.LatestIntegrity is null || now - r.LatestIntegrity.CheckedAt > thresholds.IntegrityMaxAge))
            {
                alerts.Add(new Alert($"integrity-age:{r.RegisterId}", AlertSeverity.Warning, "integrity_not_checked", r.RegisterId, subject,
                    "Chaîne d'intégrité non vérifiée récemment (tâche nocturne ?).", r.LatestIntegrity?.CheckedAt ?? now));
            }

            if (r.VisionUpToDate == false && r.VisionError is not null && r.VisionReportedAt is { } reported)
            {
                alerts.Add(new Alert($"vision:{r.RegisterId}", AlertSeverity.Warning, "vision_error", r.RegisterId, subject,
                    $"Reconnaissance : {r.VisionError}", reported));
            }
        }

        var late = snapshot.DebitsWithoutTicket.Where(d => now - d.OccurredAt > thresholds.DebitWithoutTicketAfter).ToList();
        if (late.Count > 0)
        {
            alerts.Add(new Alert("debits-without-ticket", AlertSeverity.Warning, "debit_without_ticket", null, "Rapprochement",
                string.Create(French, $"{late.Count} débit(s) de compte sans ticket reçu ({late.Sum(d => d.Amount):0.00} MAD) : voir Supervision → Rapprochement."),
                late.Min(d => d.OccurredAt)));
        }

        if (snapshot.OfflineOverdraftsLast7Days > 0)
        {
            alerts.Add(new Alert("offline-overdrafts", AlertSeverity.Info, "offline_overdraft", null, "Comptes",
                $"{snapshot.OfflineOverdraftsLast7Days} vente(s) hors ligne au-delà du découvert sur 7 jours (à régulariser).", now));
        }

        return [.. alerts.OrderByDescending(a => a.Severity).ThenBy(a => a.Subject)];
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static string Ago(DateTimeOffset now, DateTimeOffset at)
    {
        var span = now - at;
        return span.TotalHours >= 48 ? $"{(int)span.TotalDays} j" : span.TotalMinutes >= 90 ? $"{(int)span.TotalHours} h" : $"{Math.Max(0, (int)span.TotalMinutes)} min";
    }
}
