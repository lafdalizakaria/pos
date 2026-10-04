namespace Newrest.Pos.Domain.Vision;

/// <summary>One prediction and what the cashier did with it (kept as is, or corrected/removed).</summary>
public readonly record struct PredictionOutcome(decimal Confidence, bool Correct);

public sealed record ConfidenceBucket(decimal From, decimal To, int Count, int Correct)
{
    public decimal Precision => Count == 0 ? 0m : Math.Round((decimal)Correct / Count, 4);
}

/// <param name="SuggestedHigh">Lowest threshold whose auto-added lines reach the target precision (null: not enough data).</param>
/// <param name="SuggestedLow">Lowest threshold whose highlighted proposals are still right often enough to be worth a tap.</param>
public sealed record ThresholdAdvice(IReadOnlyList<ConfidenceBucket> Buckets, int Predictions, decimal? SuggestedHigh, decimal? PrecisionAtHigh,
    decimal? SuggestedLow, decimal? PrecisionAtLow);

/// <summary>
/// Calibration from the field: model confidences are compared with the cashiers' validations to suggest thresholds
/// (auto-add only where the model is almost always right; propose where it is right often enough).
/// </summary>
public static class ThresholdAdvisor
{
    public const decimal TargetAutoPrecision = 0.97m;
    public const decimal TargetProposalPrecision = 0.60m;
    public const int MinimumSamples = 30;

    public static ThresholdAdvice Advise(IReadOnlyCollection<PredictionOutcome> outcomes, decimal targetAuto = TargetAutoPrecision,
        decimal targetProposal = TargetProposalPrecision, int minimumSamples = MinimumSamples)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var buckets = Enumerable.Range(0, 10).Select(i =>
        {
            decimal from = i / 10m, to = (i + 1) / 10m;
            var inside = outcomes.Where(o => o.Confidence >= from && (o.Confidence < to || (i == 9 && o.Confidence <= 1m))).ToList();
            return new ConfidenceBucket(from, to, inside.Count, inside.Count(o => o.Correct));
        }).ToList();

        decimal? high = null, precisionHigh = null;
        for (var t = 0.50m; t <= 0.99m; t += 0.01m)
        {
            var above = outcomes.Where(o => o.Confidence >= t).ToList();
            if (above.Count < minimumSamples)
            {
                break;
            }

            var precision = (decimal)above.Count(o => o.Correct) / above.Count;
            if (precision >= targetAuto)
            {
                (high, precisionHigh) = (t, Math.Round(precision, 4));
                break;
            }
        }

        decimal? low = null, precisionLow = null;
        if (high is { } h)
        {
            for (var t = h - 0.01m; t >= 0.10m; t -= 0.01m)
            {
                var band = outcomes.Where(o => o.Confidence >= t && o.Confidence < h).ToList();
                if (band.Count < minimumSamples / 3)
                {
                    continue;
                }

                var precision = (decimal)band.Count(o => o.Correct) / band.Count;
                if (precision < targetProposal)
                {
                    break;
                }

                // The lowest confidence actually observed: thresholds between two observed values are equivalent.
                (low, precisionLow) = (Math.Floor(band.Min(o => o.Confidence) * 100) / 100, Math.Round(precision, 4));
            }
        }

        return new ThresholdAdvice(buckets, outcomes.Count, high, precisionHigh, low, precisionLow);
    }
}
