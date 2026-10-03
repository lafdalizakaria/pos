namespace Newrest.Pos.Contracts.V1;

/// <param name="AutoAcceptRate">Share of sold quantities added automatically (confidence above the high threshold).</param>
/// <param name="CorrectionRate">Share of the quantities proposed by the vision service that the cashier changed.</param>
public sealed record VisionKpiDto(string Label, int Recognitions, int TimedOut, int AverageLatencyMs, int P95LatencyMs,
    int Lines, int Auto, int Confirmed, int Corrected, int Manual, decimal AutoAcceptRate, decimal CorrectionRate);

public sealed record VisionArticleStatsDto(string ArticleCode, string ArticleName, int Sold, int Auto, int Confirmed, int Corrected, int Manual);

/// <summary>The model proposed <paramref name="Predicted"/>, the cashier sold <paramref name="Actual"/>.</summary>
public sealed record VisionConfusionDto(string Predicted, string Actual, int Count);

public sealed record VisionStatsDto(DateOnly From, DateOnly To, VisionKpiDto Total, IReadOnlyList<VisionKpiDto> BySite,
    IReadOnlyList<VisionArticleStatsDto> ByArticle, IReadOnlyList<VisionConfusionDto> Confusions, IReadOnlyList<VisionKpiDto> ByProvider,
    VisionCalibrationDto? Calibration = null);

/// <summary>Share of the predictions kept by the cashiers, by confidence band.</summary>
public sealed record VisionBucketDto(decimal From, decimal To, int Count, int Correct, decimal Precision);

/// <param name="SuggestedHigh">Lowest auto-add threshold reaching 97 % precision (null: not enough data).</param>
/// <param name="SuggestedLow">Lowest proposal threshold still right at least 60 % of the time.</param>
public sealed record VisionCalibrationDto(int Predictions, IReadOnlyList<VisionBucketDto> Buckets, decimal? SuggestedHigh, decimal? PrecisionAtHigh,
    decimal? SuggestedLow, decimal? PrecisionAtLow);

// ----- Model deployment ---------------------------------------------------------------------------------------------

public sealed record VisionModelDto(Guid Id, string Version, string Status, int ClassCount, IReadOnlyList<string> Classes, int ImageSize, long SizeBytes,
    string Sha256, decimal? Map50, string UploadedBy, DateTimeOffset UploadedAt, DateTimeOffset? PublishedAt, string? Notes);

/// <param name="UnknownArticleCodes">Classes of the model that match no article of the catalogue (warning only).</param>
public sealed record VisionModelUploadResult(VisionModelDto Model, IReadOnlyList<string> UnknownArticleCodes);

/// <param name="Configured">False: the site has never been configured; its registers keep their local settings.</param>
/// <param name="EffectiveModelVersion">Model the registers install (the chosen one, or the latest published).</param>
public sealed record SiteVisionSettingsDto(Guid SiteId, string SiteName, bool Configured, bool Enabled, string Provider, Guid? ModelId,
    string? EffectiveModelVersion, decimal LowThreshold, decimal HighThreshold, decimal HybridMinConfidence);

/// <param name="Provider">Mock, Gemini, Yolo or Hybrid.</param>
/// <param name="ModelId">Yolo/Hybrid: model to deploy; null = always the latest published model.</param>
public sealed record SiteVisionSettingsUpdate(bool Enabled, string Provider, Guid? ModelId, decimal LowThreshold, decimal HighThreshold,
    decimal HybridMinConfidence);

/// <param name="UpToDate">The register reports the provider and model expected for its site.</param>
public sealed record RegisterVisionStatusDto(Guid RegisterId, string RegisterPrefix, string RegisterName, string SiteName, string? ExpectedProvider,
    string? ExpectedModelVersion, string? Provider, string? ModelVersion, bool ServiceReachable, bool ProviderReady, string? Error,
    DateTimeOffset? ReportedAt, bool UpToDate);
