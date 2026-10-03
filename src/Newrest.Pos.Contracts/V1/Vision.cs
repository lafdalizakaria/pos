namespace Newrest.Pos.Contracts.V1;

/// <param name="AutoAcceptRate">Share of sold quantities added automatically (confidence above the high threshold).</param>
/// <param name="CorrectionRate">Share of the quantities proposed by the vision service that the cashier changed.</param>
public sealed record VisionKpiDto(string Label, int Recognitions, int TimedOut, int AverageLatencyMs, int P95LatencyMs,
    int Lines, int Auto, int Confirmed, int Corrected, int Manual, decimal AutoAcceptRate, decimal CorrectionRate);

public sealed record VisionArticleStatsDto(string ArticleCode, string ArticleName, int Sold, int Auto, int Confirmed, int Corrected, int Manual);

/// <summary>The model proposed <paramref name="Predicted"/>, the cashier sold <paramref name="Actual"/>.</summary>
public sealed record VisionConfusionDto(string Predicted, string Actual, int Count);

public sealed record VisionStatsDto(DateOnly From, DateOnly To, VisionKpiDto Total, IReadOnlyList<VisionKpiDto> BySite,
    IReadOnlyList<VisionArticleStatsDto> ByArticle, IReadOnlyList<VisionConfusionDto> Confusions, IReadOnlyList<VisionKpiDto> ByProvider);
