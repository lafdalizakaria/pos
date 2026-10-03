using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1.Sync;

namespace Newrest.Pos.Client.Core.Sync;

public static class Outbox
{
    /// <summary>Adds an item to the outbox inside the caller's transaction (same commit as the fiscal data).</summary>
    public static void Enqueue(ClientDbContext db, OutboxKind kind, Guid itemId, object payload, DateTimeOffset now) =>
        db.Outbox.Add(new OutboxItem
        {
            ItemId = itemId,
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(payload, payload.GetType(), LocalStore.Json),
            CreatedAt = now,
            Status = OutboxStatus.Pending,
        });
}

/// <summary>
/// Sends the outbox in order (sessions, movements, tickets, Z) and refreshes the reference cache. The server
/// deduplicates every item, so a resend after a lost response is harmless. A refusal stops the queue: tickets must
/// reach the server in sequence order.
/// </summary>
public sealed partial class SyncService(
    LocalStore store, PosApiClient api, ReferenceCache cache, ConnectivityState state, RegisterOptions options, TimeProvider clock,
    ILogger<SyncService> logger)
{
    /// <summary>Refusals that resolve themselves once earlier items arrive (or the server catches up).</summary>
    private static readonly HashSet<string> TransientCodes =
        ["sequence_gap", "movement_missing", "unknown_session", "tickets_missing", "unknown_original", "session_already_open"];

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly SemaphoreSlim _wakeUp = new(0, 1);
    private DateTimeOffset _lastReferenceSync = DateTimeOffset.MinValue;

    /// <summary>Asks the background loop to run now (after a sale).</summary>
    public void Trigger()
    {
        if (_wakeUp.CurrentCount == 0)
        {
            try
            {
                _wakeUp.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(forceReference: false, ct);
            try
            {
                await _wakeUp.WaitAsync(options.OutboxInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RunOnceAsync(bool forceReference = false, CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await PushOutboxAsync(ct);
            if (forceReference || clock.GetUtcNow() - _lastReferenceSync >= options.ReferenceSyncInterval)
            {
                await PullReferenceAsync(ct);
            }
        }
        catch (ServerUnreachableException ex)
        {
            state.IsOnline = false;
            LogOffline(logger, ex.Message);
        }
        finally
        {
            state.PendingCount = await store.CountPendingAsync(ct);
            _runLock.Release();
        }
    }

    public async Task PullReferenceAsync(CancellationToken ct = default)
    {
        var cursor = await store.GetSettingAsync(SettingKeys.ReferenceCursor, ct);
        var response = await api.GetReferenceAsync(cursor, ct);
        await cache.ApplyAsync(response, clock.GetUtcNow(), ct);
        await store.SetSettingAsync(SettingKeys.ReferenceCursor, response.Cursor, ct);
        await store.SetSettingAsync(SettingKeys.LastReferenceSync, response.ServerTime.ToString("O"), ct);
        _lastReferenceSync = clock.GetUtcNow();
        state.IsOnline = true;
        state.LastSyncAt = response.ServerTime;
    }

    /// <summary>Sends pending items in order; stops at the first network failure or refusal.</summary>
    public async Task<int> PushOutboxAsync(CancellationToken ct = default)
    {
        var sent = 0;
        while (true)
        {
            OutboxItem? item;
            await using (var db = store.Open())
            {
                item = await db.Outbox.AsNoTracking().Where(o => o.Status != OutboxStatus.Sent).OrderBy(o => o.Position).FirstOrDefaultAsync(ct);
            }

            if (item is null)
            {
                state.BlockingError = null;
                return sent;
            }

            if (item.Status == OutboxStatus.Rejected)
            {
                state.BlockingError = item.LastError;
                return sent;
            }

            try
            {
                await SendAsync(item, ct);
                await MarkAsync(item.Position, OutboxStatus.Sent, null, ct);
                state.IsOnline = true;
                state.BlockingError = null;
                sent++;
            }
            catch (ServerRejectedException ex)
            {
                state.IsOnline = true;
                if (item.Kind == OutboxKind.Recognition)
                {
                    // Statistics only: a refusal must never hold back fiscal data.
                    await MarkAsync(item.Position, OutboxStatus.Sent, $"skipped {ex.Code}: {ex.Detail}", ct);
                    LogRejected(logger, item.Kind, item.ItemId, ex.Code, ex.Detail);
                    continue;
                }

                var transient = TransientCodes.Contains(ex.Code);
                await MarkAsync(item.Position, transient ? OutboxStatus.Pending : OutboxStatus.Rejected, $"{ex.Code}: {ex.Detail}", ct);
                if (!transient)
                {
                    state.BlockingError = $"{item.Kind} refusé ({ex.Code})";
                    LogRejected(logger, item.Kind, item.ItemId, ex.Code, ex.Detail);
                }

                return sent;
            }
        }
    }

    /// <summary>Supervisor action after a refusal has been investigated: retry the blocked item.</summary>
    public async Task RetryRejectedAsync(CancellationToken ct = default)
    {
        await using var db = store.Open();
        await db.Outbox.Where(o => o.Status == OutboxStatus.Rejected)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OutboxStatus.Pending), ct);
        state.BlockingError = null;
    }

    private async Task SendAsync(OutboxItem item, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case OutboxKind.CashSessionOpened:
                await api.OpenCashSessionAsync(Deserialize<CashSessionSyncDto>(item), ct);
                break;
            case OutboxKind.AccountMovement:
                var movement = Deserialize<AccountMovementSyncDto>(item);
                await api.PostAccountMovementAsync(movement, ct);
                await using (var db = store.Open())
                {
                    await db.AccountMovements.Where(m => m.IdempotencyKey == movement.IdempotencyKey)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Synced, true), ct);
                }

                break;
            case OutboxKind.Ticket:
                await api.PostTicketAsync(Deserialize<TicketSyncDto>(item), ct);
                break;
            case OutboxKind.ZReport:
                await api.PostZReportAsync(Deserialize<ZReportSyncDto>(item), ct);
                break;
            case OutboxKind.Recognition:
                await api.PostRecognitionAsync(Deserialize<RecognitionSyncDto>(item), ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown outbox kind {item.Kind}.");
        }
    }

    private async Task MarkAsync(long position, OutboxStatus status, string? error, CancellationToken ct)
    {
        await using var db = store.Open();
        var now = clock.GetUtcNow();
        await db.Outbox.Where(o => o.Position == position).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.Status, status)
            .SetProperty(o => o.Attempts, o => o.Attempts + 1)
            .SetProperty(o => o.LastAttemptAt, now)
            .SetProperty(o => o.LastError, error)
            .SetProperty(o => o.SentAt, status == OutboxStatus.Sent ? now : null), ct);
    }

    private static T Deserialize<T>(OutboxItem item) => JsonSerializer.Deserialize<T>(item.PayloadJson, LocalStore.Json)!;

    [LoggerMessage(Level = LogLevel.Information, Message = "Server unreachable, working offline: {Reason}")]
    private static partial void LogOffline(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox item {Kind} {ItemId} rejected by the server: {Code} {Detail}")]
    private static partial void LogRejected(ILogger logger, OutboxKind kind, Guid itemId, string code, string? detail);
}
