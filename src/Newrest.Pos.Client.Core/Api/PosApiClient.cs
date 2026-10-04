using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Contracts;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;

namespace Newrest.Pos.Client.Core.Api;

/// <summary>The server could not be reached (network, DNS, timeout, 5xx): the register continues offline.</summary>
public sealed class ServerUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server answered with a business refusal (4xx with a problem <see cref="Code"/>).</summary>
public sealed class ServerRejectedException(HttpStatusCode status, string code, string? detail)
    : Exception($"{(int)status} {code}: {detail}")
{
    public HttpStatusCode Status { get; } = status;

    public string Code { get; } = code;

    public string? Detail { get; } = detail;
}

/// <summary>Where the device key is kept (DPAPI on Windows; memory in tests).</summary>
public interface IDeviceKeyStore
{
    string? Load();

    void Save(string key);
}

public sealed class InMemoryDeviceKeyStore(string? key = null) : IDeviceKeyStore
{
    private string? _key = key;

    public string? Load() => _key;

    public void Save(string key) => _key = key;
}

/// <summary>HTTP client of the register API. Obtains and renews its short-lived token from the device key.</summary>
public sealed class PosApiClient(HttpClient http, RegisterOptions options, IDeviceKeyStore keys, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    public Guid RegisterId { get; set; }

    public Task<RegisterTokenResponse> AuthenticateAsync(CancellationToken ct = default) => RefreshTokenAsync(force: true, ct);

    public Task<RegisterProfileDto> GetProfileAsync(CancellationToken ct = default) => SendAsync<RegisterProfileDto>(HttpMethod.Get, "register/profile", null, ct);

    public Task<ReferenceSyncResponse> GetReferenceAsync(string? cursor, CancellationToken ct = default) =>
        SendAsync<ReferenceSyncResponse>(HttpMethod.Get, cursor is null ? "register/reference" : $"register/reference?cursor={Uri.EscapeDataString(cursor)}", null, ct);

    public Task<BadgeContextDto> GetBadgeContextAsync(string number, DateOnly businessDate, CancellationToken ct = default) =>
        SendAsync<BadgeContextDto>(HttpMethod.Get, $"register/badges/{Uri.EscapeDataString(number)}?businessDate={businessDate:yyyy-MM-dd}", null, ct);

    public Task<SyncAck> OpenCashSessionAsync(CashSessionSyncDto dto, CancellationToken ct = default) =>
        SendAsync<SyncAck>(HttpMethod.Post, "register/cash-sessions", dto, ct);

    public Task<LedgerResultDto> PostAccountMovementAsync(AccountMovementSyncDto dto, CancellationToken ct = default) =>
        SendAsync<LedgerResultDto>(HttpMethod.Post, "register/account-movements", dto, ct);

    public Task<SyncAck> PostTicketAsync(TicketSyncDto dto, CancellationToken ct = default) =>
        SendAsync<SyncAck>(HttpMethod.Post, "register/tickets", dto, ct);

    public Task<SyncAck> PostZReportAsync(ZReportSyncDto dto, CancellationToken ct = default) =>
        SendAsync<SyncAck>(HttpMethod.Post, "register/z-reports", dto, ct);

    public Task<SyncAck> PostRecognitionAsync(RecognitionSyncDto dto, CancellationToken ct = default) =>
        SendAsync<SyncAck>(HttpMethod.Post, "register/recognitions", dto, ct);

    public async Task SendHeartbeatAsync(RegisterHeartbeatDto dto, CancellationToken ct = default) =>
        await SendAsync<System.Text.Json.JsonElement?>(HttpMethod.Post, "register/heartbeat", dto, ct);

    public Task<RegisterVisionConfigDto> GetVisionConfigAsync(CancellationToken ct = default) =>
        SendAsync<RegisterVisionConfigDto>(HttpMethod.Get, "register/vision", null, ct);

    public async Task ReportVisionStatusAsync(RegisterVisionReportDto report, CancellationToken ct = default) =>
        await SendAsync<JsonElement?>(HttpMethod.Post, "register/vision/status", report, ct);

    /// <summary>Streams a model file to <paramref name="destination"/> (no timeout: models weigh tens of MB).</summary>
    public async Task DownloadVisionModelAsync(Guid modelId, string destination, CancellationToken ct = default)
    {
        await RefreshTokenAsync(force: false, ct);
        var baseUrl = options.ServerUrl ?? throw new InvalidOperationException("Adresse du serveur non configurée.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, ApiVersion.BasePath.TrimStart('/') + $"/register/vision-models/{modelId}/file"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                await ReadAsync<JsonElement>(response, ct); // throws the server's refusal
            }

            await using var file = File.Create(destination);
            await response.Content.CopyToAsync(file, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            throw new ServerUnreachableException("Téléchargement du modèle interrompu.", ex);
        }
    }

    /// <summary>Reference photo of an article (null when it no longer exists).</summary>
    public async Task<byte[]?> GetPhotoAsync(Guid photoId, CancellationToken ct = default)
    {
        await RefreshTokenAsync(force: false, ct);
        var response = await SendRawAsync(HttpMethod.Get, $"register/photos/{photoId}", null, authenticated: true, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await RefreshTokenAsync(force: true, ct);
            response = await SendRawAsync(HttpMethod.Get, $"register/photos/{photoId}", null, authenticated: true, ct);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : await ReadAsync<byte[]>(response, ct);
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        await RefreshTokenAsync(force: false, ct);
        var response = await SendRawAsync(method, path, body, authenticated: true, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await RefreshTokenAsync(force: true, ct);
            response = await SendRawAsync(method, path, body, authenticated: true, ct);
        }

        using (response)
        {
            return await ReadAsync<T>(response, ct);
        }
    }

    private async Task<RegisterTokenResponse> RefreshTokenAsync(bool force, CancellationToken ct)
    {
        if (!force && _token is not null && clock.GetUtcNow() < _tokenExpiry.AddMinutes(-1))
        {
            return null!;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && _token is not null && clock.GetUtcNow() < _tokenExpiry.AddMinutes(-1))
            {
                return null!;
            }

            var key = keys.Load() ?? throw new InvalidOperationException("Cette caisse n'est pas enregistrée (clé d'appareil absente).");
            using var response = await SendRawAsync(HttpMethod.Post, "auth/register-token", new RegisterTokenRequest(RegisterId, key), authenticated: false, ct);
            var token = await ReadAsync<RegisterTokenResponse>(response, ct);
            _token = token.AccessToken;
            _tokenExpiry = token.ExpiresAt;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        var baseUrl = options.ServerUrl ?? throw new InvalidOperationException("Adresse du serveur non configurée.");
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, ApiVersion.BasePath.TrimStart('/') + "/" + path));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (authenticated && _token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            return await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ServerUnreachableException("Serveur injoignable.", ex);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return default!;
            }

            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            throw new ServerUnreachableException($"Serveur indisponible ({(int)response.StatusCode}).");
        }

        string code = "http_" + (int)response.StatusCode;
        string? detail = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (problem.ValueKind == JsonValueKind.Object)
            {
                code = problem.TryGetProperty("code", out var c) ? c.GetString() ?? code : problem.TryGetProperty("title", out var t) ? t.GetString() ?? code : code;
                detail = problem.TryGetProperty("detail", out var d) ? d.GetString() : null;
            }
        }
        catch (JsonException)
        {
        }

        throw new ServerRejectedException(response.StatusCode, code, detail);
    }
}
