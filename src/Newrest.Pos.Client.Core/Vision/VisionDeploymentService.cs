using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Contracts.V1.Sync;

namespace Newrest.Pos.Client.Core.Vision;

/// <summary>Outcome of one application of the site's vision settings (also reported to the server).</summary>
public sealed record VisionDeploymentResult(bool Configured, string? Provider, string? ModelVersion, bool ServiceReachable, bool ProviderReady,
    string? Error, bool ModelInstalled);

/// <summary>
/// Applies the vision settings chosen in the back-office for the register's site: thresholds on the sale screen,
/// model download (SHA-256 checked) and installation in the local vision service, provider switch, status report.
/// Runs after each reference synchronisation, outside the fiscal queue. Never changes anything the service refused:
/// a model that fails its checks leaves the current provider in place.
/// </summary>
public sealed partial class VisionDeploymentService(
    PosApiClient api, IVisionClient vision, VisionOptions options, RegisterOptions register, LocalStore store, ConnectivityState state,
    ILogger<VisionDeploymentService> logger)
{
    private const string ConfigKey = "vision.siteConfig";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<VisionDeploymentResult> ApplyAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var result = await ApplyCoreAsync(ct);
            state.VisionLabel = Describe(result);
            if (result.Configured)
            {
                try
                {
                    await api.ReportVisionStatusAsync(new RegisterVisionReportDto(result.Provider, result.ModelVersion, result.ServiceReachable,
                        result.ProviderReady, result.Error), ct);
                }
                catch (Exception ex) when (ex is ServerUnreachableException or ServerRejectedException)
                {
                    LogReportFailed(logger, ex.Message);
                }
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<VisionDeploymentResult> ApplyCoreAsync(CancellationToken ct)
    {
        RegisterVisionConfigDto? config;
        try
        {
            config = await api.GetVisionConfigAsync(ct);
            await store.SetSettingAsync(ConfigKey, config, ct);
        }
        catch (ServerUnreachableException)
        {
            config = await store.GetSettingAsync<RegisterVisionConfigDto>(ConfigKey, ct); // offline restart: last known settings
        }

        if (config is null || !config.Configured)
        {
            return new VisionDeploymentResult(false, null, null, false, false, null, false);
        }

        options.Enabled = config.Enabled;
        options.LowThreshold = config.LowThreshold;
        options.HighThreshold = config.HighThreshold;
        if (!config.Enabled)
        {
            return new VisionDeploymentResult(true, "désactivé", null, false, false, null, false);
        }

        VisionHealth health;
        try
        {
            health = await vision.GetHealthAsync(ct);
        }
        catch (VisionUnavailableException ex)
        {
            return new VisionDeploymentResult(true, null, null, false, false, ex.Message, false);
        }

        var installed = false;
        try
        {
            if (config.Model is { } model && !(health.ModelsInstalled ?? []).Contains(model.Version))
            {
                await InstallAsync(model, ct);
                installed = true;
            }

            var provider = config.Provider.ToLowerInvariant();
            if (!string.Equals(health.Provider, provider, StringComparison.Ordinal) || health.ModelVersion != config.Model?.Version || installed)
            {
                health = await vision.SetRuntimeAsync(new VisionRuntimeRequest(provider, config.Model?.Version, config.HybridMinConfidence), ct);
                LogSwitched(logger, provider, config.Model?.Version);
            }
        }
        catch (Exception ex) when (ex is VisionUnavailableException or ServerUnreachableException or ServerRejectedException or InvalidDataException)
        {
            LogDeploymentFailed(logger, ex.Message);
            return new VisionDeploymentResult(true, health.Provider, health.ModelVersion, true, health.ProviderReady, ex.Message, installed);
        }

        var error = health.LocalOverride ? "Moteur forcé localement (vision.json) : réglages du site ignorés."
            : health.ProviderReady ? null : health.Detail;
        return new VisionDeploymentResult(true, health.Provider, health.ModelVersion, true, health.ProviderReady, error, installed);
    }

    private async Task InstallAsync(VisionModelPackageDto model, CancellationToken ct)
    {
        var folder = Path.Combine(register.DataFolder, "models");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{model.Version}.onnx.download");
        try
        {
            await api.DownloadVisionModelAsync(model.Id, path, ct);
            await using (var file = File.OpenRead(path))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
                if (file.Length != model.SizeBytes || hash != model.Sha256)
                {
                    throw new InvalidDataException($"Modèle {model.Version} altéré pendant le téléchargement (empreinte différente) : non installé.");
                }
            }

            await using (var file = File.OpenRead(path))
            {
                await vision.InstallModelAsync(model.Version, file, model.ManifestJson, ct);
            }

            LogInstalled(logger, model.Version);
        }
        finally
        {
            File.Delete(path); // the vision service keeps its own copy
        }
    }

    private static string Describe(VisionDeploymentResult r) => !r.Configured ? "Vision : réglages locaux"
        : r.Provider == "désactivé" ? "Vision désactivée"
        : !r.ServiceReachable ? "Vision : service injoignable"
        : $"Vision : {r.Provider}{(r.ModelVersion is null ? "" : " " + r.ModelVersion)}{(r.Error is null ? "" : " ⚠")}";

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision model {Version} installed in the local service")]
    private static partial void LogInstalled(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision provider switched to {Provider} (model {Version})")]
    private static partial void LogSwitched(ILogger logger, string provider, string? version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision deployment failed: {Reason}")]
    private static partial void LogDeploymentFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision status not reported: {Reason}")]
    private static partial void LogReportFailed(ILogger logger, string reason);
}
