using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class VisionDeploymentTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    private static readonly Guid Casa = DemoDataSeeder.Id("site:CAS-SM");
    private static readonly Guid Tanger = DemoDataSeeder.Id("site:TNG-TFZ");
    private static readonly Guid Cas1 = DemoDataSeeder.Id("register:CAS1");

    private async Task<HttpClient> RegisterClientAsync(Guid registerId)
    {
        var key = await ReadAsync<DeviceKeyIssued>(await AdminClient().PostAsync($"/api/v1/registers/{registerId}/device-key", null));
        var token = await ReadAsync<RegisterTokenResponse>(await Anonymous().PostAsJsonAsync("/api/v1/auth/register-token",
            new RegisterTokenRequest(registerId, key.DeviceKey)));
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static (byte[] Model, string Manifest) Package(string version, string[] classes, byte[]? content = null, string? sha = null)
    {
        var model = content ?? Encoding.UTF8.GetBytes("onnx-bytes-" + version);
        var manifest = JsonSerializer.Serialize(new
        {
            version,
            classes,
            imgsz = 640,
            sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(model)),
            size_bytes = model.Length,
            metrics = new { map50 = 0.87 },
        });
        return (model, manifest);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, (byte[] Model, string Manifest) package)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(package.Model), "model", "model.onnx" },
            { new StringContent(package.Manifest), "manifest", "manifest.json" },
        };
        return client.PostAsync("/api/v1/vision/models", form);
    }

    private static SiteVisionSettingsUpdate Settings(string provider, Guid? model = null, decimal low = 0.6m, decimal high = 0.9m) =>
        new(true, provider, model, low, high, 0.65m);

    [Fact]
    public async Task Model_is_uploaded_checked_published_deployed_and_reported()
    {
        var admin = AdminClient();
        var upload = await UploadAsync(admin, Package("20261003-1200", ["CSC-VND", "EAU-50", "NOUVEAU"]));
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await ReadAsync<VisionModelUploadResult>(upload);
        result.Model.Should().Match<VisionModelDto>(m => m.Status == "Draft" && m.ClassCount == 3 && m.Map50 == 0.87m && m.UploadedBy == Admin);
        result.UnknownArticleCodes.Should().Equal("NOUVEAU");

        (await UploadAsync(admin, Package("20261003-1200", ["CSC-VND"]))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(await UploadAsync(admin, Package("v-bad", ["CSC-VND"], sha: new string('0', 64))))).Should().Be("checksum_mismatch");
        (await ProblemCodeAsync(await UploadAsync(admin, ([1, 2], "{\"version\": \"x\"}")))).Should().Be("invalid_manifest");
        (await UploadAsync(ManagerClient(), Package("v-manager", ["CSC-VND"]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A draft cannot be deployed.
        (await ProblemCodeAsync(await admin.PutAsJsonAsync($"/api/v1/vision/sites/{Casa}", Settings("Yolo")))).Should().Be("no_published_model");
        var register = await RegisterClientAsync(Cas1);
        (await register.GetAsync($"/api/v1/register/vision-models/{result.Model.Id}/file")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync<RegisterVisionConfigDto>(await register.GetAsync("/api/v1/register/vision"))).Configured.Should().BeFalse();

        await ReadAsync<VisionModelDto>(await admin.PostAsync($"/api/v1/vision/models/{result.Model.Id}/publish", null));
        var site = await ReadAsync<SiteVisionSettingsDto>(await ManagerClient().PutAsJsonAsync($"/api/v1/vision/sites/{Casa}", Settings("Hybrid", low: 0.55m)));
        site.Should().Match<SiteVisionSettingsDto>(s => s.Configured && s.Provider == "Hybrid" && s.ModelId == null && s.EffectiveModelVersion == "20261003-1200");
        (await ManagerClient().PutAsJsonAsync($"/api/v1/vision/sites/{Tanger}", Settings("Gemini"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(await admin.PutAsJsonAsync($"/api/v1/vision/sites/{Casa}", Settings("Gemini", low: 0.95m)))).Should().Be("invalid_thresholds");

        var config = await ReadAsync<RegisterVisionConfigDto>(await register.GetAsync("/api/v1/register/vision"));
        config.Should().Match<RegisterVisionConfigDto>(c => c.Configured && c.Enabled && c.Provider == "Hybrid" && c.LowThreshold == 0.55m
                                                           && c.HybridMinConfidence == 0.65m && c.Model!.Version == "20261003-1200");
        JsonDocument.Parse(config.Model!.ManifestJson).RootElement.GetProperty("classes").GetArrayLength().Should().Be(3);
        (await register.GetByteArrayAsync($"/api/v1/register/vision-models/{config.Model.Id}/file")).Should()
            .Equal(Encoding.UTF8.GetBytes("onnx-bytes-20261003-1200"));

        var statuses = await ReadAsync<List<RegisterVisionStatusDto>>(await admin.GetAsync("/api/v1/vision/registers"));
        statuses.Single(s => s.RegisterId == Cas1).Should().Match<RegisterVisionStatusDto>(s => !s.UpToDate && s.ExpectedProvider == "hybrid" && s.ReportedAt == null);
        (await register.PostAsJsonAsync("/api/v1/register/vision/status", new RegisterVisionReportDto("hybrid", "20261003-1200", true, true, null)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        statuses = await ReadAsync<List<RegisterVisionStatusDto>>(await admin.GetAsync("/api/v1/vision/registers"));
        statuses.Single(s => s.RegisterId == Cas1).UpToDate.Should().BeTrue();
        statuses.Single(s => s.RegisterPrefix == "TNG1").ExpectedProvider.Should().BeNull("Tangier has no site settings");

        // Retiring the only published model used by a site is refused; after publishing a newer one it is allowed.
        (await ProblemCodeAsync(await admin.PostAsync($"/api/v1/vision/models/{result.Model.Id}/retire", null))).Should().Be("model_in_use");
        var second = await ReadAsync<VisionModelUploadResult>(await UploadAsync(admin, Package("20261010-0900", ["CSC-VND", "EAU-50"])));
        await ReadAsync<VisionModelDto>(await admin.PostAsync($"/api/v1/vision/models/{second.Model.Id}/publish", null));
        (await ReadAsync<RegisterVisionConfigDto>(await register.GetAsync("/api/v1/register/vision"))).Model!.Version.Should().Be("20261010-0900",
            "sites on 'latest' follow the newest published model");
        (await ReadAsync<VisionModelDto>(await admin.PostAsync($"/api/v1/vision/models/{result.Model.Id}/retire", null))).Status.Should().Be("Retired");
        (await ProblemCodeAsync(await admin.PostAsync($"/api/v1/vision/models/{result.Model.Id}/publish", null))).Should().Be("model_retired");

        // Pinning a model, then back to Gemini: no model for the register any more.
        await ReadAsync<SiteVisionSettingsDto>(await admin.PutAsJsonAsync($"/api/v1/vision/sites/{Casa}", Settings("Yolo", second.Model.Id)));
        (await ProblemCodeAsync(await admin.PostAsync($"/api/v1/vision/models/{second.Model.Id}/retire", null))).Should().Be("model_in_use");
        await ReadAsync<SiteVisionSettingsDto>(await admin.PutAsJsonAsync($"/api/v1/vision/sites/{Casa}", Settings("Gemini", second.Model.Id)));
        (await ReadAsync<RegisterVisionConfigDto>(await register.GetAsync("/api/v1/register/vision"))).Model.Should().BeNull();

        var audit = await AdminClient().GetStringAsync("/api/v1/audit?entityType=SiteVisionSettings");
        audit.Should().Contain("Hybrid");
    }

    [Fact]
    public async Task Stats_suggest_thresholds_from_the_cashiers_validations()
    {
        var register = await RegisterClientAsync(Cas1);
        for (var i = 0; i < 40; i++)
        {
            var kept = i % 10 != 0; // 90 % of the 0.95 predictions kept; the 0.5 ones always corrected
            var dto = new RecognitionSyncDto(Guid.NewGuid(), Guid.NewGuid(), null, DateTimeOffset.UtcNow, "yolo:v1", 80, false,
                [new RecognitionPredictionDto("CSC-VND", 0.95m, null, null), new RecognitionPredictionDto("EAU-50", 0.5m, null, null)],
                [new RecognitionLineDto(kept ? "CSC-VND" : "CSC-PLT", 1, kept ? "VisionAuto" : "VisionCorrected", 0),
                 new RecognitionLineDto("SOD-33", 1, "VisionCorrected", 1)]);
            (await register.PostAsJsonAsync("/api/v1/register/recognitions", dto)).EnsureSuccessStatusCode();
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var stats = await ReadAsync<VisionStatsDto>(await AdminClient().GetAsync(
            $"/api/v1/vision/stats?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}&provider=yolo"));
        var calibration = stats.Calibration!;
        calibration.Predictions.Should().Be(80);
        calibration.Buckets.Single(b => b.From == 0.9m).Should().Match<VisionBucketDto>(b => b.Count == 40 && b.Correct == 36 && b.Precision == 0.9m);
        calibration.SuggestedHigh.Should().BeNull("90 % is below the 97 % target for automatic additions");
        (await ReadAsync<VisionStatsDto>(await AdminClient().GetAsync(
            $"/api/v1/vision/stats?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}&provider=gemini"))).Total.Recognitions.Should().Be(0);
    }
}
