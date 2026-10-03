using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;
using Newrest.Pos.Domain.Vision;

namespace Newrest.Pos.Application.Vision;

/// <summary>Ingestion of the registers' recognition logs and the back-office vision KPIs (scoped by site).</summary>
public sealed class RecognitionService(IPosDbContext db, AccessControl access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Idempotent on <see cref="RecognitionSyncDto.Id"/>. Never blocks fiscal data: the register sends it after the ticket.</summary>
    public async Task<SyncAck> IngestAsync(Guid registerId, RecognitionSyncDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var existing = await db.RecognitionLogs.AsNoTracking().Where(r => r.Id == dto.Id).Select(r => (Guid?)r.RegisterId).SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing == registerId ? new SyncAck(dto.Id, WasDuplicate: true) : throw new ConflictException("The recognition belongs to another register.");
        }

        if (!await db.Registers.AnyAsync(r => r.Id == registerId && r.IsActive, ct))
        {
            throw new ForbiddenException("Unknown or inactive register.");
        }

        if (dto.Predictions.Any(p => p.Confidence is < 0 or > 1) || dto.Lines.Any(l => l.Quantity <= 0))
        {
            throw new DomainException("invalid_recognition", "Confidences must be between 0 and 1 and quantities positive.");
        }

        var sources = dto.Lines.Select(l => (Line: l, Source: Mappings.Parse<LineSource>(l.Source, "line source"))).ToList();
        var log = new RecognitionLog(dto.Id, registerId, dto.CapturedAt, dto.Provider, dto.LatencyMs, JsonSerializer.Serialize(dto.Predictions, Json))
        {
            TimedOut = dto.TimedOut,
            ImageStorageKey = string.IsNullOrWhiteSpace(dto.DatasetId) ? null : $"register-dataset/{dto.DatasetId}",
        };
        if (dto.TicketId is { } ticketId)
        {
            log.RecordFeedback(ticketId, JsonSerializer.Serialize(dto.Lines, Json), dto.Predictions.Count,
                sources.Where(s => s.Source == LineSource.VisionAuto).Sum(s => s.Line.Quantity),
                sources.Where(s => s.Source == LineSource.VisionCorrected).Sum(s => s.Line.Quantity),
                sources.Where(s => s.Source == LineSource.Manual).Sum(s => s.Line.Quantity));
        }

        db.RecognitionLogs.Add(log);
        await db.SaveChangesAsync(ct);
        return new SyncAck(dto.Id, WasDuplicate: false);
    }

    /// <summary>KPIs over [from, to] (dates of capture, UTC), optionally for one site.</summary>
    public async Task<VisionStatsDto> GetStatsAsync(DateOnly from, DateOnly to, Guid? siteId, CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        var registers = (await (from r in db.Registers.AsNoTracking()
                                join p in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals p.Id
                                join s in db.Sites.AsNoTracking() on p.SiteId equals s.Id
                                where siteId == null || s.Id == siteId
                                select new { r.Id, SiteId = s.Id, SiteName = s.Name, s.CompanyId }).ToListAsync(ct))
            .Where(r => scope.CanAccessSite(r.CompanyId, r.SiteId)).ToDictionary(r => r.Id);
        var ids = registers.Keys.ToList();
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var logs = await db.RecognitionLogs.AsNoTracking()
            .Where(l => ids.Contains(l.RegisterId) && l.CapturedAt >= start && l.CapturedAt < end)
            .OrderBy(l => l.CapturedAt).Take(50_000).ToListAsync(ct);

        var analysed = logs.Select(l => Analyse(l, registers[l.RegisterId].SiteName)).ToList();
        var names = await db.Articles.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Name, ct);
        return new VisionStatsDto(from, to, Summarise("Total", analysed),
            [.. analysed.GroupBy(a => a.Site).OrderBy(g => g.Key).Select(g => Summarise(g.Key, [.. g]))],
            [.. analysed.SelectMany(a => a.Lines).GroupBy(l => l.Code).Select(g => new VisionArticleStatsDto(g.Key, names.GetValueOrDefault(g.Key) ?? g.Key,
                    g.Sum(l => l.Quantity), g.Where(l => l.Source == LineSource.VisionAuto).Sum(l => l.Quantity),
                    g.Where(l => l.Source == LineSource.VisionConfirmed).Sum(l => l.Quantity), g.Where(l => l.Source == LineSource.VisionCorrected).Sum(l => l.Quantity),
                    g.Where(l => l.Source == LineSource.Manual).Sum(l => l.Quantity)))
                .OrderByDescending(a => a.Corrected + a.Manual).ThenByDescending(a => a.Sold)],
            [.. analysed.SelectMany(a => a.Confusions).GroupBy(c => c).Select(g => new VisionConfusionDto(g.Key.Predicted, g.Key.Actual, g.Count()))
                .OrderByDescending(c => c.Count).Take(20)],
            [.. analysed.GroupBy(a => a.Provider).OrderBy(g => g.Key).Select(g => Summarise(g.Key, [.. g]))]);
    }

    private static VisionKpiDto Summarise(string label, IReadOnlyList<Analysed> items)
    {
        var answered = items.Where(i => !i.TimedOut).ToList();
        var lines = items.SelectMany(i => i.Lines).ToList();
        var visionLines = lines.Where(l => l.Source != LineSource.Manual).Sum(l => l.Quantity);
        var latencies = answered.Select(i => i.LatencyMs).Order().ToList();
        return new VisionKpiDto(label, items.Count, items.Count(i => i.TimedOut),
            latencies.Count == 0 ? 0 : (int)latencies.Average(), latencies.Count == 0 ? 0 : latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1],
            lines.Sum(l => l.Quantity), lines.Where(l => l.Source == LineSource.VisionAuto).Sum(l => l.Quantity),
            lines.Where(l => l.Source == LineSource.VisionConfirmed).Sum(l => l.Quantity), lines.Where(l => l.Source == LineSource.VisionCorrected).Sum(l => l.Quantity),
            lines.Where(l => l.Source == LineSource.Manual).Sum(l => l.Quantity),
            Rate(lines.Where(l => l.Source == LineSource.VisionAuto).Sum(l => l.Quantity), lines.Sum(l => l.Quantity)),
            Rate(lines.Where(l => l.Source == LineSource.VisionCorrected).Sum(l => l.Quantity), visionLines));
    }

    private static decimal Rate(int part, int total) => total == 0 ? 0m : Math.Round((decimal)part / total, 4);

    private static Analysed Analyse(RecognitionLog log, string site)
    {
        var predictions = JsonSerializer.Deserialize<List<RecognitionPredictionDto>>(log.RawPredictionsJson, Json) ?? [];
        var lines = string.IsNullOrEmpty(log.ValidatedLinesJson) ? [] : JsonSerializer.Deserialize<List<RecognitionLineDto>>(log.ValidatedLinesJson, Json) ?? [];
        var parsed = lines.Select(l => new AnalysedLine(l.ArticleCode, l.Quantity, Enum.TryParse<LineSource>(l.Source, out var s) ? s : LineSource.Manual)).ToList();
        var confusions = lines
            .Where(l => l.PredictionIndex is { } i && i >= 0 && i < predictions.Count && predictions[i].ArticleCode != l.ArticleCode)
            .Select(l => (Predicted: predictions[l.PredictionIndex!.Value].ArticleCode, Actual: l.ArticleCode)).ToList();
        return new Analysed(site, log.Provider, log.TimedOut, log.LatencyMs, parsed, confusions);
    }

    private sealed record AnalysedLine(string Code, int Quantity, LineSource Source);

    private sealed record Analysed(string Site, string Provider, bool TimedOut, int LatencyMs, List<AnalysedLine> Lines,
        List<(string Predicted, string Actual)> Confusions);
}
