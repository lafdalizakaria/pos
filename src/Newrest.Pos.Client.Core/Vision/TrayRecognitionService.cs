using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Data;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Devices.Camera;
using Newrest.Pos.Domain.Sales;
using Newrest.Pos.Domain.Vision;

namespace Newrest.Pos.Client.Core.Vision;

/// <summary>One prediction mapped to today's menu and classified by the confidence policy.</summary>
public sealed record TrayProposal(int Index, MenuButton Button, string CategoryName, decimal Confidence, RecognitionDecision Decision,
    MenuButton? Alternative, decimal? AlternativeConfidence);

/// <summary>Result of one capture. <see cref="Failure"/> set = nothing proposed, the cashier enters the tray by hand.</summary>
public sealed record TrayRecognition(Guid Id, string? RecognitionId, string Provider, int ElapsedMs, int ServiceLatencyMs, DateTimeOffset CapturedAt,
    IReadOnlyList<TrayProposal> Proposals, IReadOnlyList<VisionItem> RawItems, string? Failure)
{
    public bool Failed => Failure is not null;
}

/// <summary>
/// Assisted sale: capture → local vision service → proposals classified by the thresholds. Bounded by
/// <see cref="VisionOptions.Timeout"/>: whatever happens (camera, service, model), the sale continues by hand.
/// After the sale, the validated lines go back to the vision service (dataset) and to the server (KPIs, via the outbox).
/// </summary>
public sealed partial class TrayRecognitionService(
    IVisionClient vision, ICamera camera, ReferencePhotoCache photos, ReferenceCache cache, LocalStore store, RegisterOptions options, PosApiClient api,
    TimeProvider clock, ILogger<TrayRecognitionService> logger)
{
    private static readonly TimeSpan FeedbackTimeout = TimeSpan.FromSeconds(3);

    public bool IsEnabled => options.Vision.Enabled;

    /// <summary>Thresholds of the site (updated by <see cref="VisionDeploymentService"/>), or of the local configuration.</summary>
    public RecognitionConfidencePolicy Policy => new(options.Vision.LowThreshold, options.Vision.HighThreshold);

    public async Task<TrayRecognition> RecognizeAsync(IReadOnlyList<MenuCategoryGroup> menu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(menu);
        var started = Stopwatch.GetTimestamp();
        var capturedAt = clock.GetUtcNow();
        var buttons = menu.SelectMany(g => g.Items.Select(b => (Group: g.Name, Button: b)))
            .GroupBy(x => x.Button.Item.ArticleCode.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        if (buttons.Count == 0)
        {
            return Failed("Aucun article au menu du jour.");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.Vision.Timeout);
        try
        {
            var image = await camera.CaptureAsync(budget.Token);
            var articles = await cache.GetArticlesAsync(budget.Token);
            var candidates = buttons.Select(b => new VisionCandidate(b.Key, b.Value.Button.Item.ArticleName, b.Value.Group,
                articles.GetValueOrDefault(b.Value.Button.Item.ArticleId)?.VisualDescription)).ToList();
            var references = options.Vision.SendReferencePhotos
                ? buttons.Select(b => (Code: b.Key, Photos: photos.Load(articles.GetValueOrDefault(b.Value.Button.Item.ArticleId)?.PhotoIds,
                        options.Vision.MaxReferencePhotosPerArticle)))
                    .Where(x => x.Photos.Count > 0).ToDictionary(x => x.Code, x => x.Photos)
                : [];
            var response = await vision.RecognizeAsync(image, candidates, references, api.RegisterId.ToString(), budget.Token);

            var proposals = new List<TrayProposal>();
            for (var i = 0; i < response.Items.Count; i++)
            {
                var item = response.Items[i];
                if (!buttons.TryGetValue(item.ArticleCode.ToUpperInvariant(), out var match))
                {
                    continue; // already filtered by the service; defensive
                }

                var confidence = Math.Clamp(item.Confidence, 0m, 1m);
                var alternative = item.Alternatives?.Select(a => (Alt: a, Found: buttons.TryGetValue(a.ArticleCode.ToUpperInvariant(), out var m) ? m : default))
                    .FirstOrDefault(a => a.Found.Button is not null && a.Found.Button.Item.ArticleCode != match.Button.Item.ArticleCode);
                proposals.Add(new TrayProposal(i, match.Button, match.Group, confidence, Policy.Classify(confidence), alternative?.Found.Button,
                    alternative?.Found.Button is null ? null : Math.Clamp(alternative.Value.Alt.Confidence, 0m, 1m)));
            }

            return new TrayRecognition(Guid.CreateVersion7(), response.RecognitionId, response.Provider, Elapsed(started), response.LatencyMs, capturedAt,
                proposals, response.Items, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed($"Reconnaissance trop lente (> {options.Vision.Timeout.TotalSeconds:0} s) : saisir le plateau.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Camera, service, model: whatever fails, the cashier continues by hand.
            LogFailure(logger, ex.Message);
            return Failed(ex is VisionUnavailableException ? ex.Message + " Saisir le plateau." : "Reconnaissance indisponible : saisir le plateau.");
        }

        TrayRecognition Failed(string reason) =>
            new(Guid.CreateVersion7(), null, "none", Elapsed(started), Elapsed(started), capturedAt, [], [], reason);
    }

    /// <summary>
    /// Lines finally sold, per prediction: each prediction kept, confirmed or corrected keeps its index (its box becomes a
    /// training label); units the model did not see are reported as manual.
    /// </summary>
    public static IReadOnlyList<RecognitionLineDto> BuildLines(IEnumerable<CartLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<RecognitionLineDto>();
        foreach (var line in lines.Where(l => l.Quantity > 0))
        {
            foreach (var (index, source) in line.Predictions.Take(line.Quantity))
            {
                result.Add(new RecognitionLineDto(line.Code, 1, source.ToString(), index));
            }

            var rest = line.Quantity - Math.Min(line.Quantity, line.Predictions.Count);
            if (rest > 0)
            {
                result.Add(new RecognitionLineDto(line.Code, rest, nameof(LineSource.Manual), null));
            }
        }

        return result;
    }

    /// <summary>Records the outcome (outbox → server KPIs) and sends the feedback to the vision service (dataset, best effort).</summary>
    public async Task RecordOutcomeAsync(TrayRecognition recognition, Guid? ticketId, IReadOnlyList<CartLine> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        var validated = ticketId is null ? [] : BuildLines(lines);
        var provider = recognition.Provider.Length > 32 ? recognition.Provider[..32] : recognition.Provider;
        var dto = new RecognitionSyncDto(recognition.Id, ticketId, recognition.RecognitionId, recognition.CapturedAt, provider,
            recognition.Failed ? recognition.ElapsedMs : recognition.ServiceLatencyMs, recognition.Failed,
            [.. recognition.RawItems.Select(i => new RecognitionPredictionDto(i.ArticleCode, Math.Clamp(i.Confidence, 0m, 1m),
                i.Alternatives?.FirstOrDefault()?.ArticleCode, i.Alternatives?.FirstOrDefault()?.Confidence))],
            validated);
        await using (var db = store.Open())
        {
            Outbox.Enqueue(db, OutboxKind.Recognition, dto.Id, dto, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }

        if (recognition.RecognitionId is { } id && ticketId is { } ticket)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FeedbackTimeout);
            var sent = await vision.SendFeedbackAsync(new VisionFeedback(id, ticket.ToString(),
                [.. validated.Select(l => new VisionFeedbackLine(l.ArticleCode, l.Quantity, l.Source, l.PredictionIndex))]), timeout.Token);
            if (!sent)
            {
                LogFeedbackLost(logger, id);
            }
        }
    }

    /// <summary>Downloads the reference photos of today's articles that are not cached yet (in the background, outside the 6 s budget).</summary>
    public async Task PrefetchPhotosAsync(IEnumerable<MenuButton> items, CancellationToken ct = default)
    {
        if (!IsEnabled || !options.Vision.SendReferencePhotos)
        {
            return;
        }

        var articles = await cache.GetArticlesAsync(ct);
        var ids = items.SelectMany(i => (articles.GetValueOrDefault(i.Item.ArticleId)?.PhotoIds ?? []).Take(options.Vision.MaxReferencePhotosPerArticle));
        await photos.PrefetchAsync(ids, ct);
    }

    private static int Elapsed(long started) => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tray recognition failed, manual entry: {Reason}")]
    private static partial void LogFailure(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision feedback for {RecognitionId} not delivered (service unreachable)")]
    private static partial void LogFeedbackLost(ILogger logger, string recognitionId);
}

/// <summary>Reference photos of the articles, downloaded once from the server and kept in the register's data folder.</summary>
public sealed partial class ReferencePhotoCache(PosApiClient api, RegisterOptions options, ILogger<ReferencePhotoCache> logger)
{
    private readonly SemaphoreSlim _prefetch = new(1, 1);

    private string Folder => Path.Combine(options.DataFolder, "photos");

    public IReadOnlyList<byte[]> Load(IReadOnlyList<Guid>? ids, int max)
    {
        if (ids is null)
        {
            return [];
        }

        var result = new List<byte[]>();
        foreach (var id in ids.Take(max))
        {
            var path = Path.Combine(Folder, $"{id:N}.img");
            if (File.Exists(path))
            {
                result.Add(File.ReadAllBytes(path));
            }
        }

        return result;
    }

    /// <summary>One download pass at a time (the sale screen and a manual refresh may overlap).</summary>
    public async Task<int> PrefetchAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        await _prefetch.WaitAsync(ct);
        try
        {
            return await DownloadMissingAsync(ids, ct);
        }
        finally
        {
            _prefetch.Release();
        }
    }

    private async Task<int> DownloadMissingAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var downloaded = 0;
        foreach (var id in ids.Distinct())
        {
            var path = Path.Combine(Folder, $"{id:N}.img");
            if (File.Exists(path))
            {
                continue;
            }

            try
            {
                if (await api.GetPhotoAsync(id, ct) is { } bytes)
                {
                    var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
                    await File.WriteAllBytesAsync(temporary, bytes, ct);
                    File.Move(temporary, path, overwrite: true);
                    downloaded++;
                }
            }
            catch (ServerUnreachableException ex)
            {
                LogPrefetchStopped(logger, ex.Message);
                break;
            }
            catch (ServerRejectedException ex)
            {
                LogPrefetchStopped(logger, ex.Message);
            }
        }

        return downloaded;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reference photo download stopped: {Reason}")]
    private static partial void LogPrefetchStopped(ILogger logger, string reason);
}
