using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Client.Core.Vision;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Scenarios.Tests;

[Collection(ScenarioCollection.Name)]
public sealed class VisionDeploymentScenarios(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Casa = DemoDataSeeder.Id("site:CAS-SM");
    private static readonly Guid Cas1 = DemoDataSeeder.Id("register:CAS1");
    private TestDatabase _database = null!;
    private ApiFactory _api = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync(seedDemo: true);
        _api = new ApiFactory(_database.ConnectionString);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    /// <summary>Back-office: upload + publish a model package, returns its id.</summary>
    internal static async Task<Guid> PublishModelAsync(HttpClient admin, string version, byte[] model, string? manifest = null)
    {
        manifest ??= JsonSerializer.Serialize(new
        {
            version,
            classes = new[] { "CSC-VND", "EAU-50" },
            imgsz = 320,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(model)),
            size_bytes = model.Length,
            metrics = new { map50 = 0.93 },
        });
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(model), "model", "model.onnx" },
            { new StringContent(manifest), "manifest", "manifest.json" },
        };
        var upload = await (await admin.PostAsync("/api/v1/vision/models", form)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<VisionModelUploadResult>();
        (await admin.PostAsync($"/api/v1/vision/models/{upload!.Model.Id}/publish", null)).EnsureSuccessStatusCode();
        return upload.Model.Id;
    }

    internal static async Task ConfigureSiteAsync(HttpClient admin, Guid site, SiteVisionSettingsUpdate settings) =>
        (await admin.PutAsJsonAsync($"/api/v1/vision/sites/{site}", settings)).EnsureSuccessStatusCode();

    private static async Task SyncAsync(RegisterHarness register)
    {
        var sync = register.Get<SyncService>();
        await sync.RunOnceAsync(forceReference: true);
        await sync.VisionDeployment;
    }

    private Task<RegisterHarness> RegisterAsync(FakeVisionClient vision) =>
        RegisterHarness.CreateAsync(_api, Cas1, configure: s => s.AddSingleton<IVisionClient>(vision));

    private async Task<RegisterVisionStatusDto> StatusAsync() =>
        (await _api.Admin().GetFromJsonAsync<List<RegisterVisionStatusDto>>("/api/v1/vision/registers"))!.Single(r => r.RegisterId == Cas1);

    [Fact]
    public async Task Site_settings_deploy_the_model_switch_the_provider_and_apply_the_thresholds()
    {
        var admin = _api.Admin();
        var bytes = Encoding.UTF8.GetBytes("model-v1-" + new string('x', 5000));
        var v1 = await PublishModelAsync(admin, "20261003-1200", bytes);
        await ConfigureSiteAsync(admin, Casa, new SiteVisionSettingsUpdate(true, "Hybrid", null, 0.5m, 0.85m, 0.7m));

        var vision = new FakeVisionClient();
        await using var register = await RegisterAsync(vision);
        await SyncAsync(register);

        vision.Installed.Should().ContainKey("20261003-1200").WhoseValue.Should().Equal(bytes);
        vision.RuntimeRequests.Should().ContainSingle().Which.Should().Be(new VisionRuntimeRequest("hybrid", "20261003-1200", 0.7m));
        var policy = register.Get<TrayRecognitionService>().Policy;
        (policy.LowThreshold, policy.HighThreshold).Should().Be((0.5m, 0.85m));
        register.Get<ConnectivityState>().VisionLabel.Should().Be("Vision : hybrid 20261003-1200");
        (await StatusAsync()).Should().Match<RegisterVisionStatusDto>(s => s.UpToDate && s.Provider == "hybrid" && s.ModelVersion == "20261003-1200");
        Directory.EnumerateFiles(Path.Combine(register.Folder, "models")).Should().BeEmpty("the download is removed once installed");

        await SyncAsync(register);
        vision.RuntimeRequests.Should().HaveCount(1, "nothing to do when the service already matches the site");

        // Back to Gemini from the back-office: provider switched, model kept installed (rollback without download).
        await ConfigureSiteAsync(admin, Casa, new SiteVisionSettingsUpdate(true, "Gemini", null, 0.6m, 0.9m, 0.6m));
        await SyncAsync(register);
        vision.Provider.Should().Be("gemini");
        vision.ModelVersion.Should().BeNull();
        await ConfigureSiteAsync(admin, Casa, new SiteVisionSettingsUpdate(true, "Yolo", v1, 0.6m, 0.9m, 0.6m));
        await SyncAsync(register);
        (vision.Provider, vision.ModelVersion).Should().Be(("yolo", "20261003-1200"));
        vision.Installed.Should().HaveCount(1);

        // A newer model published: sites on "latest" upgrade, the pinned site does not.
        await PublishModelAsync(admin, "20261010-0800", Encoding.UTF8.GetBytes("model-v2"));
        await SyncAsync(register);
        vision.ModelVersion.Should().Be("20261003-1200");
        await ConfigureSiteAsync(admin, Casa, new SiteVisionSettingsUpdate(true, "Yolo", null, 0.6m, 0.9m, 0.6m));
        await SyncAsync(register);
        vision.ModelVersion.Should().Be("20261010-0800");
        vision.Installed.Keys.Should().BeEquivalentTo(["20261003-1200", "20261010-0800"]);
    }

    [Fact]
    public async Task Tampered_model_is_not_installed_and_the_current_provider_stays()
    {
        var admin = _api.Admin();
        var bytes = Encoding.UTF8.GetBytes("model-tampered-" + Guid.NewGuid());
        await PublishModelAsync(admin, "20261003-1300", bytes);
        // Someone alters the file in the server storage after upload.
        var stored = Directory.GetFiles(Path.Combine(_api.StoragePath, "vision-models", "20261003-1300")).Single();
        await File.WriteAllBytesAsync(stored, Encoding.UTF8.GetBytes("evil-" + Guid.NewGuid()));
        await ConfigureSiteAsync(admin, Casa, new SiteVisionSettingsUpdate(true, "Yolo", null, 0.6m, 0.9m, 0.6m));

        var vision = new FakeVisionClient();
        await using var register = await RegisterAsync(vision);
        await SyncAsync(register);

        vision.Installed.Should().BeEmpty();
        vision.RuntimeRequests.Should().BeEmpty();
        vision.Provider.Should().Be("gemini");
        var status = await StatusAsync();
        status.UpToDate.Should().BeFalse();
        status.Error.Should().Contain("altéré");
        register.Get<ConnectivityState>().VisionLabel.Should().EndWith("⚠");
    }

    [Fact]
    public async Task Disabled_site_turns_the_photo_button_off_and_errors_are_reported()
    {
        var admin = _api.Admin();
        await ConfigureSiteAsync(admin, Casa, new SiteVisionSettingsUpdate(false, "Gemini", null, 0.6m, 0.9m, 0.6m));
        var vision = new FakeVisionClient();
        await using var register = await RegisterAsync(vision);
        var (_, _) = await register.LoginAndOpenAsync();
        await SyncAsync(register);
        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();
        screen.VisionEnabled.Should().BeFalse();
        screen.CaptureTrayCommand.CanExecute(null).Should().BeFalse();
        register.Get<ConnectivityState>().VisionLabel.Should().Be("Vision désactivée");

        await ConfigureSiteAsync(admin, Casa, new SiteVisionSettingsUpdate(true, "Gemini", null, 0.6m, 0.9m, 0.6m));
        vision.Reachable = false;
        await SyncAsync(register);
        register.Get<TrayRecognitionService>().IsEnabled.Should().BeTrue("re-enabled from the back-office");
        (await StatusAsync()).Should().Match<RegisterVisionStatusDto>(s => !s.ServiceReachable && s.Error == "Service de reconnaissance injoignable.");

        vision.Reachable = true;
        vision.LocalOverride = true;
        vision.Provider = "mock";
        await SyncAsync(register);
        (await StatusAsync()).Error.Should().Contain("forcé localement");
    }

    [Fact]
    public async Task Without_site_settings_the_register_keeps_its_local_configuration()
    {
        var vision = new FakeVisionClient();
        await using var register = await RegisterAsync(vision);
        await SyncAsync(register);
        vision.RuntimeRequests.Should().BeEmpty();
        register.Get<ConnectivityState>().VisionLabel.Should().Be("Vision : réglages locaux");
        (await StatusAsync()).ReportedAt.Should().BeNull();
    }
}
