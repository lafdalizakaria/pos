using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Vision;

public enum RecognitionDecision
{
    /// <summary>Confidence &gt;= high threshold: the line is added automatically.</summary>
    AutoAccept,

    /// <summary>Between thresholds: the line is added highlighted, the second choice is one tap away.</summary>
    NeedsConfirmation,

    /// <summary>Below the low threshold: only the category is suggested; the cashier picks from the menu.</summary>
    ManualSelection,
}

/// <summary>Confidence thresholds of the assisted sale (defaults 0.60 / 0.90, configurable per site).</summary>
public sealed record RecognitionConfidencePolicy
{
    public const decimal DefaultLow = 0.60m;
    public const decimal DefaultHigh = 0.90m;

    public RecognitionConfidencePolicy(decimal lowThreshold = DefaultLow, decimal highThreshold = DefaultHigh)
    {
        if (lowThreshold is < 0 or > 1 || highThreshold is < 0 or > 1 || lowThreshold > highThreshold)
        {
            throw new DomainException("invalid_thresholds", "Thresholds must satisfy 0 <= low <= high <= 1.");
        }

        LowThreshold = lowThreshold;
        HighThreshold = highThreshold;
    }

    public decimal LowThreshold { get; }
    public decimal HighThreshold { get; }

    public RecognitionDecision Classify(decimal confidence)
    {
        if (confidence is < 0 or > 1)
        {
            throw new DomainException("invalid_confidence", "Confidence must be between 0 and 1.");
        }

        return confidence >= HighThreshold ? RecognitionDecision.AutoAccept
            : confidence >= LowThreshold ? RecognitionDecision.NeedsConfirmation
            : RecognitionDecision.ManualSelection;
    }
}

/// <summary>One recognition attempt and what the cashier finally validated (dataset + KPIs).</summary>
public sealed class RecognitionLog : Entity
{
    private RecognitionLog()
    {
    }

    public RecognitionLog(Guid id, Guid registerId, DateTimeOffset capturedAt, string provider, int latencyMs, string rawPredictionsJson)
        : base(id)
    {
        RegisterId = Guard.NotEmpty(registerId, nameof(registerId));
        CapturedAt = capturedAt;
        Provider = Guard.NotBlank(provider, nameof(provider), 32);
        LatencyMs = latencyMs < 0 ? throw new DomainException("invalid_latency", "Latency cannot be negative.") : latencyMs;
        RawPredictionsJson = rawPredictionsJson ?? "[]";
    }

    public Guid RegisterId { get; private set; }
    public Guid? TicketId { get; private set; }
    public DateTimeOffset CapturedAt { get; private set; }
    public string Provider { get; private set; } = null!;
    public int LatencyMs { get; private set; }
    public bool TimedOut { get; set; }

    /// <summary>Key of the cropped tray image in the dataset storage (never a face: crop is enforced at capture).</summary>
    public string? ImageStorageKey { get; set; }

    public string RawPredictionsJson { get; private set; } = null!;
    public string? ValidatedLinesJson { get; private set; }
    public int PredictedCount { get; private set; }
    public int AutoAcceptedCount { get; private set; }
    public int CorrectedCount { get; private set; }
    public int AddedManuallyCount { get; private set; }

    public void RecordFeedback(Guid ticketId, string validatedLinesJson, int predicted, int autoAccepted, int corrected, int addedManually)
    {
        if (predicted < 0 || autoAccepted < 0 || corrected < 0 || addedManually < 0)
        {
            throw new DomainException("invalid_feedback", "Feedback counters cannot be negative.");
        }

        TicketId = Guard.NotEmpty(ticketId, nameof(ticketId));
        ValidatedLinesJson = Guard.NotBlank(validatedLinesJson, nameof(validatedLinesJson), int.MaxValue);
        PredictedCount = predicted;
        AutoAcceptedCount = autoAccepted;
        CorrectedCount = corrected;
        AddedManuallyCount = addedManually;
    }
}
