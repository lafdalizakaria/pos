using System.Net.Http.Json;
using Newrest.Pos.Application.Operations;
using Newrest.Pos.Domain.Operations;

namespace Newrest.Pos.Api.Operations;

/// <summary>Section <c>Supervision</c> of the API configuration.</summary>
public sealed class SupervisionOptions
{
    /// <summary>Background jobs run in ONE instance only (set false on the other instances behind a load balancer).</summary>
    public bool JobsEnabled { get; set; } = true;

    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>UTC time of the nightly verification of every ticket chain.</summary>
    public TimeOnly IntegrityCheckTimeUtc { get; set; } = new(2, 30);

    /// <summary>Incoming webhook (Teams, Slack...) receiving new Warning/Critical alerts as <c>{"text": ...}</c>. Secret.</summary>
    public Uri? WebhookUrl { get; set; }

    /// <summary>A still active alert is notified again after this delay.</summary>
    public TimeSpan RenotifyAfter { get; set; } = TimeSpan.FromHours(6);

    public string BackOfficeUrl { get; set; } = "";

    public SupervisionThresholds Thresholds { get; set; } = new();
}

/// <summary>Evaluates alerts every few minutes (metrics + webhook) and verifies every ticket chain each night.</summary>
public sealed partial class SupervisionWorker(IServiceScopeFactory scopes, SupervisionOptions options, IHttpClientFactory http, TimeProvider clock,
    ILogger<SupervisionWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _notified = [];
    private DateOnly _lastIntegrityRun = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.JobsEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.EvaluationInterval, clock);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            await using var scope = scopes.CreateAsyncScope();
            var supervision = scope.ServiceProvider.GetRequiredService<SupervisionService>();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (_lastIntegrityRun < today && TimeOnly.FromDateTime(now.UtcDateTime) >= options.IntegrityCheckTimeUtc)
            {
                var run = await supervision.RunIntegrityChecksSystemAsync(ct);
                _lastIntegrityRun = today;
                LogIntegrity(logger, run.RegistersChecked, run.Invalid);
            }

            var alerts = await supervision.EvaluateSystemAsync(ct);
            await NotifyAsync(alerts, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
        }
    }

    private async Task NotifyAsync(IReadOnlyList<Alert> alerts, DateTimeOffset now, CancellationToken ct)
    {
        var active = alerts.Where(a => a.Severity >= AlertSeverity.Warning).ToList();
        foreach (var gone in _notified.Keys.Except(active.Select(a => a.Key)).ToList())
        {
            _notified.Remove(gone);
        }

        var fresh = active.Where(a => !_notified.TryGetValue(a.Key, out var at) || now - at >= options.RenotifyAfter).ToList();
        if (fresh.Count == 0 || options.WebhookUrl is null)
        {
            return;
        }

        var lines = fresh.Take(20).Select(a => $"{(a.Severity == AlertSeverity.Critical ? "🔴" : "🟠")} {a.Subject} — {a.Message}");
        var text = $"Newrest POS : {fresh.Count} alerte(s)\n" + string.Join("\n", lines)
                   + (string.IsNullOrEmpty(options.BackOfficeUrl) ? "" : $"\n{options.BackOfficeUrl.TrimEnd('/')}/supervision");
        try
        {
            using var response = await http.CreateClient(nameof(SupervisionWorker)).PostAsJsonAsync(options.WebhookUrl, new { text }, ct);
            response.EnsureSuccessStatusCode();
            foreach (var alert in fresh)
            {
                _notified[alert.Key] = now;
            }
        }
        catch (HttpRequestException ex)
        {
            LogWebhookFailed(logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Integrity check: {Registers} register(s), {Invalid} invalid chain(s)")]
    private static partial void LogIntegrity(ILogger logger, int registers, int invalid);

    [LoggerMessage(Level = LogLevel.Error, Message = "Supervision run failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert webhook failed: {Reason}")]
    private static partial void LogWebhookFailed(ILogger logger, string reason);
}
