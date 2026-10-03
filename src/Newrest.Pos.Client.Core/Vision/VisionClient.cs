using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Devices;
using Newrest.Pos.Devices.Camera;

namespace Newrest.Pos.Client.Core.Vision;

// Contract of the local vision service (vision/app/models.py), snake_case JSON.

public sealed record VisionCandidate(string ArticleCode, string Label, string? Category, string? VisualDescription);

public sealed record VisionAlternative(string ArticleCode, decimal Confidence);

public sealed record VisionItem(string ArticleCode, decimal Confidence, IReadOnlyList<int>? Bbox, IReadOnlyList<VisionAlternative>? Alternatives);

public sealed record VisionResponse(string RecognitionId, IReadOnlyList<VisionItem> Items, string Provider, int LatencyMs);

public sealed record VisionFeedbackLine(string ArticleCode, int Quantity, string Source, int? PredictionIndex);

public sealed record VisionFeedback(string RecognitionId, string? TicketId, IReadOnlyList<VisionFeedbackLine> Lines);

/// <summary>The vision service did not answer usefully (timeout, not running, error): the cashier continues by hand.</summary>
public sealed class VisionUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface IVisionClient
{
    /// <param name="referencePhotos">JPEG photos by article code (sent as file parts <c>reference_&lt;code&gt;</c>).</param>
    Task<VisionResponse> RecognizeAsync(CapturedImage image, IReadOnlyList<VisionCandidate> candidates,
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> referencePhotos, string? registerId, CancellationToken ct);

    /// <summary>Best effort: false when the service is unreachable (the server keeps its own copy of the outcome).</summary>
    Task<bool> SendFeedbackAsync(VisionFeedback feedback, CancellationToken ct);
}

/// <summary>HTTP client of the local vision service.</summary>
public sealed class VisionClient(HttpClient http, VisionOptions options) : IVisionClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<VisionResponse> RecognizeAsync(CapturedImage image, IReadOnlyList<VisionCandidate> candidates,
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> referencePhotos, string? registerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(referencePhotos);
        using var content = new MultipartFormDataContent();
        var jpeg = new ByteArrayContent(image.Jpeg);
        jpeg.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(jpeg, "image", "tray.jpg");
        content.Add(new StringContent(JsonSerializer.Serialize(candidates, Json)), "candidates");
        if (registerId is not null)
        {
            content.Add(new StringContent(registerId), "register_id");
        }

        foreach (var (code, photos) in referencePhotos)
        {
            var index = 0;
            foreach (var photo in photos)
            {
                var part = new ByteArrayContent(photo);
                part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                content.Add(part, "reference_" + code, $"{code}-{index++}.jpg");
            }
        }

        try
        {
            using var response = await http.PostAsync(new Uri(options.BaseUrl, "recognize"), content, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new VisionUnavailableException(await DescribeAsync(response, ct));
            }

            return await response.Content.ReadFromJsonAsync<VisionResponse>(Json, ct) ?? throw new VisionUnavailableException("Réponse vide.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new VisionUnavailableException("Service de reconnaissance injoignable.", ex);
        }
    }

    public async Task<bool> SendFeedbackAsync(VisionFeedback feedback, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(options.BaseUrl, "feedback"), feedback, Json, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (body.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("code", out var code))
            {
                return code.GetString() switch
                {
                    "timeout" => "Reconnaissance trop lente.",
                    "provider_unavailable" => "Reconnaissance non configurée sur ce poste.",
                    "invalid_image" => "Image de la caméra illisible.",
                    "face_detected" => "Visage détecté dans l'image : faire recadrer la caméra (responsable).",
                    var other => $"Reconnaissance indisponible ({other}).",
                };
            }
        }
        catch (JsonException)
        {
        }

        return $"Reconnaissance indisponible (HTTP {(int)response.StatusCode}).";
    }
}

/// <summary>Camera read through the local vision service (OpenCV, fixed exposure, tray crop configured there).</summary>
public sealed class VisionServiceCamera(HttpClient http, VisionOptions options, TimeProvider clock) : ICamera
{
    private DeviceStatus _status = DeviceStatus.Unknown;

    public string Name => "Caméra plateau (service vision)";

    public DeviceStatus Status => _status;

    public async Task<CapturedImage> CaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(options.BaseUrl, "camera/capture"), cancellationToken);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable || !response.IsSuccessStatusCode)
            {
                _status = DeviceStatus.Error;
                throw new VisionUnavailableException("Caméra indisponible.");
            }

            var jpeg = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            _status = DeviceStatus.Ready;
            return new CapturedImage(jpeg, Header(response, "X-Image-Width"), Header(response, "X-Image-Height"), clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _status = DeviceStatus.Offline;
            throw new VisionUnavailableException("Service de reconnaissance injoignable.", ex);
        }
    }

    private static int Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var value) ? value : 0;
}
