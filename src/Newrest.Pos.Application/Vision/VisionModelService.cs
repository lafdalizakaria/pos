using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Vision;

namespace Newrest.Pos.Application.Vision;

/// <summary>
/// Trained models (upload, publish, retire), vision settings per site (provider, model, thresholds) and what each
/// register reports after applying them. Registers pull their site's settings and the model file; nothing is pushed
/// from the server, so a register offline simply applies the change when it comes back.
/// </summary>
public sealed class VisionModelService(IPosDbContext db, AccessControl access, AuditTrail audit, IFileStorage storage, ICurrentUser user,
    TimeProvider clock)
{
    public const long MaxModelBytes = 300L * 1024 * 1024;

    // ----- Models (administrators) -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<VisionModelDto>> ListModelsAsync(CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var models = await db.VisionModels.AsNoTracking().OrderByDescending(m => m.CreatedAt).ToListAsync(ct);
        return [.. models.Select(ToDto)];
    }

    /// <summary>Stores the ONNX file after checking it against the manifest produced by the training (size, SHA-256).</summary>
    public async Task<VisionModelUploadResult> UploadAsync(Stream model, string manifestJson, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        ArgumentNullException.ThrowIfNull(model);
        var manifest = ParseManifest(manifestJson);
        if (await db.VisionModels.AnyAsync(m => m.Version == manifest.Version, ct))
        {
            throw new ConflictException($"The model version {manifest.Version} already exists.");
        }

        var key = $"vision-models/{manifest.Version}/{Guid.CreateVersion7():N}.onnx";
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size;
        await using (var hashing = new HashingStream(model, sha, MaxModelBytes))
        {
            try
            {
                await storage.SaveAsync(key, hashing, ct);
            }
            catch
            {
                await storage.DeleteAsync(key, CancellationToken.None);
                throw;
            }

            size = hashing.BytesRead;
        }

        var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (size != manifest.SizeBytes || hash != manifest.Sha256)
        {
            await storage.DeleteAsync(key, ct);
            throw new DomainException("checksum_mismatch", "The model file does not match its manifest (size or SHA-256).");
        }

        var entity = new VisionModel(Guid.CreateVersion7(), manifest.Version, manifest.Classes, manifest.ImageSize, hash, size, key, manifest.Map50,
            manifestJson, user.Name);
        db.VisionModels.Add(entity);
        audit.Record(AuditActions.Created, nameof(VisionModel), entity.Id, after: new { entity.Version, entity.ClassCount, entity.Map50, entity.Sha256 });
        await db.SaveChangesAsync(ct);
        var known = await db.Articles.AsNoTracking().Where(a => manifest.Classes.Contains(a.Code)).Select(a => a.Code).ToListAsync(ct);
        return new VisionModelUploadResult(ToDto(entity), [.. manifest.Classes.Except(known)]);
    }

    public async Task<VisionModelDto> PublishAsync(Guid id, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var model = await db.VisionModels.SingleOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("VisionModel", id);
        model.Publish(clock.GetUtcNow());
        audit.Record(AuditActions.Updated, nameof(VisionModel), id, after: new { Status = model.Status.ToString() });
        await db.SaveChangesAsync(ct);
        return ToDto(model);
    }

    /// <summary>Withdraws a model, unless a site uses it (explicitly, or as the only published model for "latest").</summary>
    public async Task<VisionModelDto> RetireAsync(Guid id, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var model = await db.VisionModels.SingleOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("VisionModel", id);
        if (await db.SiteVisionSettings.AnyAsync(s => s.ModelId == id, ct))
        {
            throw new DomainException("model_in_use", "A site uses this model: choose another model for it first.");
        }

        var latest = await LatestPublishedAsync(ct);
        if (latest?.Id == id && await db.SiteVisionSettings.AnyAsync(s => s.ModelId == null && (s.Provider == VisionProvider.Yolo
                || s.Provider == VisionProvider.Hybrid), ct)
            && !await db.VisionModels.AnyAsync(m => m.Id != id && m.Status == VisionModelStatus.Published, ct))
        {
            throw new DomainException("model_in_use", "Sites use the latest published model and no other model is published.");
        }

        model.Retire();
        audit.Record(AuditActions.Updated, nameof(VisionModel), id, after: new { Status = model.Status.ToString() });
        await db.SaveChangesAsync(ct);
        return ToDto(model);
    }

    // ----- Site settings -----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SiteVisionSettingsDto>> ListSiteSettingsAsync(CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        var sites = (await db.Sites.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Where(s => scope.CanAccessSite(s.CompanyId, s.Id)).ToList();
        var ids = sites.Select(s => s.Id).ToList();
        var settings = await db.SiteVisionSettings.AsNoTracking().Where(s => ids.Contains(s.SiteId)).ToDictionaryAsync(s => s.SiteId, ct);
        var models = await db.VisionModels.AsNoTracking().ToDictionaryAsync(m => m.Id, ct);
        var latest = await LatestPublishedAsync(ct);
        return [.. sites.Select(site => settings.TryGetValue(site.Id, out var s)
            ? new SiteVisionSettingsDto(site.Id, site.Name, true, s.Enabled, s.Provider.ToString(), s.ModelId, Effective(s, models, latest)?.Version,
                s.LowThreshold, s.HighThreshold, s.HybridMinConfidence)
            : new SiteVisionSettingsDto(site.Id, site.Name, false, true, nameof(VisionProvider.Gemini), null, null,
                RecognitionConfidencePolicy.DefaultLow, RecognitionConfidencePolicy.DefaultHigh, RecognitionConfidencePolicy.DefaultLow))];
    }

    public async Task<SiteVisionSettingsDto> UpdateSiteSettingsAsync(Guid siteId, SiteVisionSettingsUpdate request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await access.EnsureCanAccessSiteAsync(siteId, write: true, ct);
        var provider = Mappings.Parse<VisionProvider>(request.Provider, "vision provider");
        if (provider is VisionProvider.Yolo or VisionProvider.Hybrid)
        {
            var model = request.ModelId is { } m
                ? await db.VisionModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == m, ct) ?? throw new NotFoundException("VisionModel", m)
                : await LatestPublishedAsync(ct) ?? throw new DomainException("no_published_model", "No published model: publish one first.");
            if (model.Status != VisionModelStatus.Published)
            {
                throw new DomainException("model_not_published", "Only a published model can be deployed.");
            }
        }

        var settings = await db.SiteVisionSettings.SingleOrDefaultAsync(s => s.SiteId == siteId, ct);
        var before = settings is null ? null : new { settings.Enabled, Provider = settings.Provider.ToString(), settings.ModelId, settings.LowThreshold, settings.HighThreshold };
        if (settings is null)
        {
            settings = new SiteVisionSettings(Guid.CreateVersion7(), siteId);
            db.SiteVisionSettings.Add(settings);
        }

        settings.Update(request.Enabled, provider, request.ModelId, request.LowThreshold, request.HighThreshold, request.HybridMinConfidence);
        audit.Record(AuditActions.Updated, nameof(SiteVisionSettings), siteId, before,
            new { settings.Enabled, Provider = provider.ToString(), settings.ModelId, settings.LowThreshold, settings.HighThreshold, settings.HybridMinConfidence });
        await db.SaveChangesAsync(ct);
        return (await ListSiteSettingsAsync(ct)).Single(s => s.SiteId == siteId);
    }

    public async Task<IReadOnlyList<RegisterVisionStatusDto>> ListRegisterStatusAsync(CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        var registers = (await (from r in db.Registers.AsNoTracking()
                                join p in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals p.Id
                                join s in db.Sites.AsNoTracking() on p.SiteId equals s.Id
                                where r.IsActive
                                orderby r.TicketPrefix
                                select new { r.Id, r.TicketPrefix, r.Name, SiteId = s.Id, SiteName = s.Name, s.CompanyId }).ToListAsync(ct))
            .Where(r => scope.CanAccessSite(r.CompanyId, r.SiteId)).ToList();
        var ids = registers.Select(r => r.Id).ToList();
        var statuses = await db.RegisterVisionStatuses.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var settings = await db.SiteVisionSettings.AsNoTracking().ToDictionaryAsync(s => s.SiteId, ct);
        var models = await db.VisionModels.AsNoTracking().ToDictionaryAsync(m => m.Id, ct);
        var latest = await LatestPublishedAsync(ct);
        return [.. registers.Select(r =>
        {
            var expected = settings.GetValueOrDefault(r.SiteId);
            var expectedProvider = expected is null ? null : expected.Enabled ? expected.Provider.ToString().ToLowerInvariant() : "désactivé";
            var expectedModel = expected is null ? null : Effective(expected, models, latest)?.Version;
            var status = statuses.GetValueOrDefault(r.Id);
            var upToDate = status is not null && status.ProviderReady && (expected is null || !expected.Enabled
                || (string.Equals(status.Provider, expectedProvider, StringComparison.OrdinalIgnoreCase) && status.ModelVersion == expectedModel));
            return new RegisterVisionStatusDto(r.Id, r.TicketPrefix, r.Name, r.SiteName, expectedProvider, expectedModel, status?.Provider, status?.ModelVersion,
                status?.ServiceReachable ?? false, status?.ProviderReady ?? false, status?.Error, status?.ReportedAt, upToDate);
        })];
    }

    // ----- Registers ---------------------------------------------------------------------------------------------------

    public async Task<RegisterVisionConfigDto> GetRegisterConfigAsync(Guid registerId, CancellationToken ct = default)
    {
        var siteId = await (from r in db.Registers.AsNoTracking()
                            join p in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals p.Id
                            where r.Id == registerId && r.IsActive
                            select (Guid?)p.SiteId).SingleOrDefaultAsync(ct) ?? throw new ForbiddenException("Unknown or inactive register.");
        var settings = await db.SiteVisionSettings.AsNoTracking().SingleOrDefaultAsync(s => s.SiteId == siteId, ct);
        if (settings is null)
        {
            return new RegisterVisionConfigDto(false, true, nameof(VisionProvider.Gemini), RecognitionConfidencePolicy.DefaultLow,
                RecognitionConfidencePolicy.DefaultHigh, RecognitionConfidencePolicy.DefaultLow, null);
        }

        var model = settings.Provider is VisionProvider.Yolo or VisionProvider.Hybrid
            ? settings.ModelId is { } id ? await db.VisionModels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct) : await LatestPublishedAsync(ct)
            : null;
        return new RegisterVisionConfigDto(true, settings.Enabled, settings.Provider.ToString(), settings.LowThreshold, settings.HighThreshold,
            settings.HybridMinConfidence,
            model is null ? null : new VisionModelPackageDto(model.Id, model.Version, model.Sha256, model.SizeBytes, model.ManifestJson));
    }

    /// <summary>Model file for a register: only a published model (or the one its site explicitly uses).</summary>
    public async Task<Stream?> OpenModelForRegisterAsync(Guid registerId, Guid modelId, CancellationToken ct = default)
    {
        var config = await GetRegisterConfigAsync(registerId, ct);
        var model = await db.VisionModels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null || (model.Status != VisionModelStatus.Published && config.Model?.Id != modelId))
        {
            return null;
        }

        return await storage.OpenReadAsync(model.StoragePath, ct);
    }

    public async Task ReportRegisterStatusAsync(Guid registerId, RegisterVisionReportDto report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!await db.Registers.AnyAsync(r => r.Id == registerId && r.IsActive, ct))
        {
            throw new ForbiddenException("Unknown or inactive register.");
        }

        var status = await db.RegisterVisionStatuses.SingleOrDefaultAsync(s => s.Id == registerId, ct);
        if (status is null)
        {
            status = new RegisterVisionStatus(registerId);
            db.RegisterVisionStatuses.Add(status);
        }

        status.Report(Trim(report.Provider), Trim(report.ModelVersion), report.ServiceReachable, report.ProviderReady, report.Error, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length > 32 ? value[..32] : value;

    private Task<VisionModel?> LatestPublishedAsync(CancellationToken ct) =>
        db.VisionModels.AsNoTracking().Where(m => m.Status == VisionModelStatus.Published).OrderByDescending(m => m.PublishedAt).FirstOrDefaultAsync(ct);

    private static VisionModel? Effective(SiteVisionSettings settings, IReadOnlyDictionary<Guid, VisionModel> models, VisionModel? latest) =>
        settings.Provider is not (VisionProvider.Yolo or VisionProvider.Hybrid) ? null
        : settings.ModelId is { } id ? models.GetValueOrDefault(id) : latest;

    private static VisionModelDto ToDto(VisionModel m) => new(m.Id, m.Version, m.Status.ToString(), m.ClassCount, m.Classes, m.ImageSize, m.SizeBytes,
        m.Sha256, m.Map50, m.UploadedBy, m.CreatedAt, m.PublishedAt, m.Notes);

    private sealed record Manifest(string Version, IReadOnlyList<string> Classes, int ImageSize, string Sha256, long SizeBytes, decimal? Map50);

    private static Manifest ParseManifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var classes = root.GetProperty("classes").EnumerateArray().Select(c => c.GetString()!.Trim().ToUpperInvariant()).ToList();
            decimal? map50 = root.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object
                             && metrics.TryGetProperty("map50", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDecimal() : null;
            return new Manifest(VisionModel.ValidateVersion(root.GetProperty("version").GetString()!), classes,
                root.TryGetProperty("imgsz", out var size) ? size.GetInt32() : 640, root.GetProperty("sha256").GetString()!.ToLowerInvariant(),
                root.GetProperty("size_bytes").GetInt64(), map50);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException)
        {
            throw new DomainException("invalid_manifest", "manifest.json is not a valid training manifest (version, classes, sha256, size_bytes).");
        }
    }

    /// <summary>Hashes while the storage reads, and refuses files over the size limit.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash, long limit) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            return Count(buffer.Span[..read]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(ReadOnlySpan<byte> data)
        {
            BytesRead += data.Length;
            if (BytesRead > limit)
            {
                throw new DomainException("invalid_file", $"Model files are limited to {limit / 1024 / 1024} MB.");
            }

            hash.AppendData(data);
            return data.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
