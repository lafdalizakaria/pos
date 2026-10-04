using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Devices.Camera;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Scenarios.Tests;

/// <summary>Runs only where the vision service environment exists (<c>cd vision &amp;&amp; uv sync</c>), e.g. CI.</summary>
public sealed class VisionServiceFactAttribute : FactAttribute
{
    public VisionServiceFactAttribute()
    {
        if (RealVisionService.Python is null)
        {
            Skip = "vision/.venv absent (cd vision && uv sync)";
        }
    }
}

/// <summary>The real FastAPI service (provider mock) started as a process on a free loopback port.</summary>
public sealed class RealVisionService : IAsyncDisposable
{
    private readonly Process _process;

    private RealVisionService(Process process, Uri url, string dataset)
    {
        _process = process;
        Url = url;
        Dataset = dataset;
    }

    public static string? VisionFolder { get; } = FindVisionFolder();

    public static string? Python => VisionFolder is null ? null
        : new[] { Path.Combine(VisionFolder, ".venv", "bin", "python"), Path.Combine(VisionFolder, ".venv", "Scripts", "python.exe") }.FirstOrDefault(File.Exists);

    public Uri Url { get; }

    public string Dataset { get; }

    public static async Task<RealVisionService> StartAsync()
    {
        var port = FreePort();
        var dataset = Path.Combine(Path.GetTempPath(), "pos-vision-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo(Python!, "-m app.main") { WorkingDirectory = VisionFolder!, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["VISION_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["VISION_PROVIDER"] = "mock";
        info.Environment["VISION_MOCK_LATENCY_MS"] = "50";
        info.Environment["VISION_DATASET_DIR"] = dataset;
        info.Environment["VISION_CAMERA_INDEX"] = "99";
        info.Environment["VISION_MODELS_DIR"] = Path.Combine(dataset, "models");
        info.Environment["VISION_RUNTIME_FILE"] = Path.Combine(dataset, "runtime.json");
        info.Environment["VISION_CONFIG_FILE"] = "";
        var process = Process.Start(info)!;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var url = new Uri($"http://127.0.0.1:{port}/");
        using var http = new HttpClient();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                if ((await http.GetAsync(new Uri(url, "health"))).IsSuccessStatusCode)
                {
                    return new RealVisionService(process, url, dataset);
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(200);
        }

        process.Kill();
        throw new InvalidOperationException("Vision service did not start.");
    }

    /// <summary>A valid JPEG (random pixels) produced by the service's own OpenCV.</summary>
    public static string MakeTrayImage(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "tray.jpg");
        using var p = Process.Start(new ProcessStartInfo(Python!, ["-c",
            $"import cv2, numpy as np; cv2.imwrite(r'{path}', np.random.default_rng(4).integers(0, 255, (900, 1200, 3), dtype=np.uint8))"])
        { WorkingDirectory = VisionFolder! })!;
        p.WaitForExit();
        return path;
    }

    /// <summary>A small ONNX model with the YOLO output layout and a fixed answer (vision/tests/onnx_models.py).</summary>
    public static byte[] MakeYoloLikeModel(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "model.onnx");
        using var p = Process.Start(new ProcessStartInfo(Python!, ["-c",
            "import sys; from tests.onnx_models import yolo_like_model; "
            + "open(sys.argv[1], 'wb').write(yolo_like_model(['CSC-VND', 'EAU-50'], "
            + "[(100, 120, 90, 80, {'CSC-VND': 0.96, 'EAU-50': 0.02}), (230, 200, 60, 70, {'EAU-50': 0.94})], 320))", path])
        { WorkingDirectory = VisionFolder! })!;
        p.WaitForExit();
        return File.ReadAllBytes(path);
    }

    public async ValueTask DisposeAsync()
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
        try
        {
            Directory.Delete(Dataset, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? FindVisionFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "vision", "app", "main.py");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "vision");
            }
        }

        return null;
    }
}

[Collection(ScenarioCollection.Name)]
public sealed class RealVisionServiceScenario(SqlServerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private ApiFactory _api = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync(seedDemo: true);
        _api = new ApiFactory(_database.ConnectionString);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [VisionServiceFact]
    public async Task Register_and_real_vision_service_complete_an_assisted_sale_and_feed_the_dataset()
    {
        await using var service = await RealVisionService.StartAsync();
        var images = Path.Combine(service.Dataset, "camera");
        RealVisionService.MakeTrayImage(images);
        var options = new RegisterOptions { Vision = { BaseUrl = service.Url } };
        await using var register = await RegisterHarness.CreateAsync(_api, DemoDataSeeder.Id("register:CAS1"), options,
            configure: s => s.AddSingleton<ICamera>(new SimulatedCamera(images)));
        await register.LoginAndOpenAsync();
        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();

        var stopwatch = Stopwatch.StartNew();
        await screen.CaptureTrayCommand.ExecuteAsync(null);
        screen.Error.Should().BeNull();
        screen.Message.Should().StartWith("Plateau reconnu en");
        (screen.Cart.Lines.Count + screen.Hints.Count).Should().BeInRange(1, 3);
        foreach (var hint in screen.Hints.ToList())
        {
            screen.AddItemCommand.Execute(hint.Predicted);
        }

        screen.AddItemCommand.Execute(screen.Groups.SelectMany(g => g.Items).Single(i => i.Item.ArticleCode == "PAIN"));
        screen.Tendered = 200m;
        await screen.PayCashCommand.ExecuteAsync(null);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        screen.Message.Should().StartWith("Ticket CAS1-00000001");
        await screen.LastOutcome;

        var record = Directory.GetFiles(service.Dataset, "*.json", SearchOption.AllDirectories).Should().ContainSingle().Subject;
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(record));
        json.RootElement.GetProperty("register_id").GetString().Should().Be(DemoDataSeeder.Id("register:CAS1").ToString());
        json.RootElement.GetProperty("validated_lines").GetArrayLength().Should().BeGreaterThan(1);
        json.RootElement.GetProperty("labels").GetArrayLength().Should().BeGreaterThan(0, "validated predictions become YOLO labels");
        File.Exists(Path.ChangeExtension(record, ".jpg")).Should().BeTrue();

        // The service has no camera here: the default camera (read through the service) reports it, the sale goes on.
        await using var withServiceCamera = await RegisterHarness.CreateAsync(_api, DemoDataSeeder.Id("register:CAS2"),
            new RegisterOptions { Vision = { BaseUrl = service.Url } },
            configure: s => s.AddSingleton<ICamera>(sp => new Client.Core.Vision.VisionServiceCamera(new HttpClient(), options.Vision, TimeProvider.System)));
        await withServiceCamera.LoginAndOpenAsync();
        var screen2 = withServiceCamera.Get<SaleViewModel>();
        await screen2.LoadAsync();
        await screen2.CaptureTrayCommand.ExecuteAsync(null);
        screen2.Error.Should().Be("Caméra indisponible. Saisir le plateau.");
    }

    [VisionServiceFact]
    public async Task Model_published_in_the_back_office_is_deployed_to_the_real_service_and_used_for_the_sale()
    {
        await using var service = await RealVisionService.StartAsync();
        var images = Path.Combine(service.Dataset, "camera");
        RealVisionService.MakeTrayImage(images);
        var admin = _api.Admin();
        await VisionDeploymentScenarios.PublishModelAsync(admin, "20261003-1500", RealVisionService.MakeYoloLikeModel(Path.Combine(service.Dataset, "pkg")));
        await VisionDeploymentScenarios.ConfigureSiteAsync(admin, DemoDataSeeder.Id("site:CAS-SM"),
            new Contracts.V1.SiteVisionSettingsUpdate(true, "Yolo", null, 0.6m, 0.9m, 0.6m));

        var options = new RegisterOptions { Vision = { BaseUrl = service.Url } };
        await using var register = await RegisterHarness.CreateAsync(_api, DemoDataSeeder.Id("register:CAS1"), options,
            configure: s => s.AddSingleton<ICamera>(new SimulatedCamera(images)));
        await register.LoginAndOpenAsync();
        var sync = register.Get<Client.Core.Sync.SyncService>();
        await sync.RunOnceAsync(forceReference: true);
        await sync.VisionDeployment;
        register.Get<Client.Core.Local.ConnectivityState>().VisionLabel.Should().Be("Vision : yolo 20261003-1500");
        using (var http = new HttpClient())
        {
            var health = await http.GetFromJsonAsync<JsonElement>(new Uri(service.Url, "health"));
            health.GetProperty("provider").GetString().Should().Be("yolo");
            health.GetProperty("provider_ready").GetBoolean().Should().BeTrue();
        }

        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();
        await screen.CaptureTrayCommand.ExecuteAsync(null);
        screen.Error.Should().BeNull();
        screen.Cart.Lines.Select(l => (l.Code, l.Source.ToString())).Should().Equal(("CSC-VND", "VisionAuto"), ("EAU-50", "VisionAuto"));
        screen.Tendered = 100m;
        await screen.PayCashCommand.ExecuteAsync(null);
        screen.Message.Should().StartWith("Ticket CAS1-00000001");
        await screen.LastOutcome;

        for (var i = 0; i < 20 && await register.Get<Client.Core.Local.LocalStore>().CountPendingAsync() > 0; i++)
        {
            await sync.RunOnceAsync();
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var stats = await admin.GetFromJsonAsync<Contracts.V1.VisionStatsDto>($"/api/v1/vision/stats?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        stats!.ByProvider.Should().ContainSingle().Which.Label.Should().Be("yolo:20261003-1500");
        var status = (await admin.GetFromJsonAsync<List<Contracts.V1.RegisterVisionStatusDto>>("/api/v1/vision/registers"))!
            .Single(r => r.RegisterPrefix == "CAS1");
        status.UpToDate.Should().BeTrue();
    }
}
