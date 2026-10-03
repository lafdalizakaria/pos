using System.Text.Json;
using System.Text.RegularExpressions;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Vision;

/// <summary>Recognition engine run by the registers' local vision service.</summary>
public enum VisionProvider
{
    Mock,
    Gemini,
    Yolo,
    Hybrid,
}

public enum VisionModelStatus
{
    /// <summary>Uploaded, not yet deployable.</summary>
    Draft,

    /// <summary>Deployable to the registers.</summary>
    Published,

    /// <summary>Withdrawn: no longer offered to the registers (kept for traceability).</summary>
    Retired,
}

/// <summary>A trained YOLO model (ONNX file + manifest produced by <c>python -m training train</c>).</summary>
public sealed partial class VisionModel : ReferenceEntity
{
    private VisionModel()
    {
    }

    public VisionModel(Guid id, string version, IReadOnlyList<string> classes, int imageSize, string sha256, long sizeBytes, string storagePath,
        decimal? map50, string manifestJson, string uploadedBy) : base(id)
    {
        Version = ValidateVersion(version);
        ArgumentNullException.ThrowIfNull(classes);
        var codes = classes.Select(c => Guard.NotBlank(c, nameof(classes), 32).ToUpperInvariant()).ToList();
        if (codes.Count == 0 || codes.Distinct().Count() != codes.Count)
        {
            throw new DomainException("invalid_model", "A model needs unique article codes as classes.");
        }

        ClassesJson = JsonSerializer.Serialize(codes);
        ClassCount = codes.Count;
        ImageSize = imageSize is >= 32 and <= 2048 ? imageSize : throw new DomainException("invalid_model", "Invalid input size.");
        Sha256 = Sha256Pattern().IsMatch(sha256 ?? "") ? sha256! : throw new DomainException("invalid_model", "Invalid SHA-256.");
        SizeBytes = sizeBytes > 0 ? sizeBytes : throw new DomainException("invalid_model", "Empty model file.");
        StoragePath = Guard.NotBlank(storagePath, nameof(storagePath), 500);
        Map50 = map50 is null or (>= 0 and <= 1) ? map50 : throw new DomainException("invalid_model", "mAP50 must be between 0 and 1.");
        ManifestJson = Guard.NotBlank(manifestJson, nameof(manifestJson), int.MaxValue);
        UploadedBy = Guard.NotBlank(uploadedBy, nameof(uploadedBy));
        Status = VisionModelStatus.Draft;
    }

    public string Version { get; private set; } = null!;
    public string ClassesJson { get; private set; } = null!;
    public int ClassCount { get; private set; }
    public int ImageSize { get; private set; }
    public string Sha256 { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public string StoragePath { get; private set; } = null!;

    /// <summary>mAP50 on the validation split at training time (quality indicator shown before publishing).</summary>
    public decimal? Map50 { get; private set; }

    /// <summary>The manifest as produced by the training, forwarded unchanged to the registers.</summary>
    public string ManifestJson { get; private set; } = null!;

    public string UploadedBy { get; private set; } = null!;
    public VisionModelStatus Status { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? Notes { get; set; }

    public IReadOnlyList<string> Classes => JsonSerializer.Deserialize<List<string>>(ClassesJson) ?? [];

    public void Publish(DateTimeOffset now)
    {
        if (Status == VisionModelStatus.Retired)
        {
            throw new DomainException("model_retired", "A retired model cannot be published again; upload a new version.");
        }

        if (Status == VisionModelStatus.Draft)
        {
            Status = VisionModelStatus.Published;
            PublishedAt = now;
        }
    }

    public void Retire() => Status = VisionModelStatus.Retired;

    public static string ValidateVersion(string version) =>
        VersionPattern().IsMatch(version ?? "") ? version! : throw new DomainException("invalid_version", "Version: 1-32 letters, digits, '.', '_' or '-'.");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();
}

/// <summary>Vision settings of a site, applied by its registers at each synchronisation.</summary>
public sealed class SiteVisionSettings : ReferenceEntity
{
    private SiteVisionSettings()
    {
    }

    public SiteVisionSettings(Guid id, Guid siteId) : base(id)
    {
        SiteId = Guard.NotEmpty(siteId, nameof(siteId));
        Enabled = true;
        Provider = VisionProvider.Gemini;
        LowThreshold = RecognitionConfidencePolicy.DefaultLow;
        HighThreshold = RecognitionConfidencePolicy.DefaultHigh;
        HybridMinConfidence = RecognitionConfidencePolicy.DefaultLow;
    }

    public Guid SiteId { get; private set; }
    public bool Enabled { get; private set; }
    public VisionProvider Provider { get; private set; }

    /// <summary>Model used by the yolo/hybrid providers; null = latest published model.</summary>
    public Guid? ModelId { get; private set; }

    public decimal LowThreshold { get; private set; }
    public decimal HighThreshold { get; private set; }

    /// <summary>Hybrid: below this YOLO confidence Gemini is asked.</summary>
    public decimal HybridMinConfidence { get; private set; }

    public void Update(bool enabled, VisionProvider provider, Guid? modelId, decimal low, decimal high, decimal hybridMinConfidence)
    {
        _ = new RecognitionConfidencePolicy(low, high); // validates 0 <= low <= high <= 1
        if (hybridMinConfidence is < 0 or > 1)
        {
            throw new DomainException("invalid_thresholds", "The hybrid confidence must be between 0 and 1.");
        }

        Enabled = enabled;
        Provider = provider;
        ModelId = provider is VisionProvider.Yolo or VisionProvider.Hybrid ? modelId : null;
        LowThreshold = low;
        HighThreshold = high;
        HybridMinConfidence = hybridMinConfidence;
    }
}

/// <summary>What a register reports after applying its site's settings (one row per register).</summary>
public sealed class RegisterVisionStatus : Entity
{
    private RegisterVisionStatus()
    {
    }

    public RegisterVisionStatus(Guid registerId) : base(Guard.NotEmpty(registerId, nameof(registerId)))
    {
    }

    public Guid RegisterId => Id;
    public string? Provider { get; private set; }
    public string? ModelVersion { get; private set; }
    public bool ServiceReachable { get; private set; }
    public bool ProviderReady { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset ReportedAt { get; private set; }

    public void Report(string? provider, string? modelVersion, bool serviceReachable, bool providerReady, string? error, DateTimeOffset now)
    {
        Provider = provider is null ? null : Guard.NotBlank(provider, nameof(provider), 32);
        ModelVersion = modelVersion is null ? null : Guard.NotBlank(modelVersion, nameof(modelVersion), 32);
        ServiceReachable = serviceReachable;
        ProviderReady = providerReady;
        Error = error is null ? null : error.Length > 500 ? error[..500] : error;
        ReportedAt = now;
    }
}
