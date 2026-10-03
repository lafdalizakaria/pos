namespace Newrest.Pos.Client.Core.Configuration;

/// <summary>Tray recognition at the register (local vision service). Section <c>Register:Vision</c>.</summary>
public sealed class VisionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Local service (Windows service on the same machine, loopback only).</summary>
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:8765/");

    /// <summary>Capture + recognition budget. Beyond it the cashier enters the tray by hand: vision never blocks a sale.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>Below: only the category is suggested (manual choice).</summary>
    public decimal LowThreshold { get; set; } = 0.60m;

    /// <summary>At or above: the line is added automatically. Between both: added highlighted with the second choice one tap away.</summary>
    public decimal HighThreshold { get; set; } = 0.90m;

    /// <summary>Reference photos of today's articles are attached to each recognition (downloaded once and cached).</summary>
    public bool SendReferencePhotos { get; set; } = true;

    public int MaxReferencePhotosPerArticle { get; set; } = 2;
}
