using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class VisionTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<HttpClient> RegisterClientAsync(Guid registerId)
    {
        var key = await ReadAsync<DeviceKeyIssued>(await AdminClient().PostAsync($"/api/v1/registers/{registerId}/device-key", null));
        var token = await ReadAsync<RegisterTokenResponse>(await Anonymous().PostAsJsonAsync("/api/v1/auth/register-token",
            new RegisterTokenRequest(registerId, key.DeviceKey)));
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static RecognitionSyncDto Recognition(Guid? ticket, int latency, bool timedOut, params RecognitionLineDto[] lines) => new(
        Guid.NewGuid(), ticket, "abc", DateTimeOffset.UtcNow, timedOut ? "none" : "gemini", latency, timedOut,
        timedOut ? [] : [new RecognitionPredictionDto("CSC-VND", 0.95m, null, null), new RecognitionPredictionDto("FRU-SAI", 0.7m, "YAO-NAT", 0.2m)],
        lines);

    private Task<VisionStatsDto> StatsAsync(HttpClient client, Guid? site = null) =>
        client.GetFromJsonAsync<VisionStatsDto>($"/api/v1/vision/stats?from={Today.AddDays(-1):yyyy-MM-dd}&to={Today.AddDays(1):yyyy-MM-dd}"
                                                + (site is null ? "" : $"&siteId={site}"))!;

    [Fact]
    public async Task Recognitions_are_ingested_idempotently_and_summarised_per_site_and_article()
    {
        var cas1 = await RegisterClientAsync(DemoDataSeeder.Id("register:CAS1"));
        var tanger = await RegisterClientAsync(DemoDataSeeder.Id("register:TNG1"));
        var sold = Recognition(Guid.NewGuid(), 1500, false,
            new RecognitionLineDto("CSC-VND", 1, "VisionAuto", 0), new RecognitionLineDto("YAO-NAT", 1, "VisionCorrected", 1),
            new RecognitionLineDto("PAIN", 2, "Manual", null));
        (await ReadAsync<SyncAck>(await cas1.PostAsJsonAsync("/api/v1/register/recognitions", sold))).WasDuplicate.Should().BeFalse();
        (await ReadAsync<SyncAck>(await cas1.PostAsJsonAsync("/api/v1/register/recognitions", sold))).WasDuplicate.Should().BeTrue();
        (await tanger.PostAsJsonAsync("/api/v1/register/recognitions", sold)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<SyncAck>(await cas1.PostAsJsonAsync("/api/v1/register/recognitions",
            Recognition(Guid.NewGuid(), 6000, true, new RecognitionLineDto("CSC-PLT", 1, "Manual", null))))).WasDuplicate.Should().BeFalse();
        (await ReadAsync<SyncAck>(await tanger.PostAsJsonAsync("/api/v1/register/recognitions",
            Recognition(Guid.NewGuid(), 900, false, new RecognitionLineDto("CSC-VND", 1, "VisionConfirmed", 0))))).WasDuplicate.Should().BeFalse();

        var all = await StatsAsync(AdminClient());
        all.Total.Should().Match<VisionKpiDto>(k => k.Recognitions == 3 && k.TimedOut == 1 && k.Lines == 6 && k.Auto == 1 && k.Confirmed == 1
                                                    && k.Corrected == 1 && k.Manual == 3 && k.AverageLatencyMs == 1200 && k.P95LatencyMs == 1500);
        all.Total.AutoAcceptRate.Should().Be(0.1667m);
        all.Total.CorrectionRate.Should().Be(0.3333m);
        all.BySite.Should().HaveCount(2);
        all.ByArticle.First().ArticleCode.Should().Be("PAIN", "the most corrected or manual articles come first");
        all.ByArticle.Single(a => a.ArticleCode == "CSC-VND").Should().Match<VisionArticleStatsDto>(a => a.Sold == 2 && a.Auto == 1 && a.Confirmed == 1);
        all.Confusions.Should().ContainSingle().Which.Should().Be(new VisionConfusionDto("FRU-SAI", "YAO-NAT", 1));
        all.ByProvider.Select(p => p.Label).Should().BeEquivalentTo(["gemini", "none"]);

        var casaOnly = await StatsAsync(ManagerClient());
        casaOnly.Total.Recognitions.Should().Be(2, "the Casablanca manager does not see Tangier");
        (await StatsAsync(AdminClient(), DemoDataSeeder.Id("site:TNG-TFZ"))).Total.Recognitions.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_recognitions_and_wrong_callers_are_refused()
    {
        var cas1 = await RegisterClientAsync(DemoDataSeeder.Id("register:CAS1"));
        var bad = Recognition(Guid.NewGuid(), 10, false, new RecognitionLineDto("CSC-VND", 0, "VisionAuto", 0));
        (await cas1.PostAsJsonAsync("/api/v1/register/recognitions", bad)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var unknownSource = Recognition(Guid.NewGuid(), 10, false, new RecognitionLineDto("CSC-VND", 1, "Telepathy", 0));
        (await cas1.PostAsJsonAsync("/api/v1/register/recognitions", unknownSource)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await AdminClient().PostAsJsonAsync("/api/v1/register/recognitions", Recognition(null, 1, true))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cas1.GetAsync($"/api/v1/vision/stats?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Registers_download_reference_photos_listed_in_their_reference_data()
    {
        var article = DemoDataSeeder.Id("article:CSC-VND");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(content, "file", "csc.jpg");
        var photo = await ReadAsync<ArticlePhotoDto>(await AdminClient().PostAsync($"/api/v1/articles/{article}/photos", form));

        var register = await RegisterClientAsync(DemoDataSeeder.Id("register:CAS1"));
        var reference = await ReadAsync<ReferenceSyncResponse>(await register.GetAsync("/api/v1/register/reference"));
        reference.Articles.Single(a => a.Id == article).Should().Match<SyncArticleDto>(a => a.PhotoIds!.Single() == photo.Id && a.VisualDescription != null);
        (await register.GetByteArrayAsync($"/api/v1/register/photos/{photo.Id}")).Should().Equal(0xFF, 0xD8, 0xFF, 0xD9);
        (await register.GetAsync($"/api/v1/register/photos/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Anonymous().GetAsync($"/api/v1/register/photos/{photo.Id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Removing the photo moves the article in the incremental sync.
        (await AdminClient().DeleteAsync($"/api/v1/articles/{article}/photos/{photo.Id}")).EnsureSuccessStatusCode();
        var next = await ReadAsync<ReferenceSyncResponse>(await register.GetAsync($"/api/v1/register/reference?cursor={Uri.EscapeDataString(reference.Cursor)}"));
        next.Articles.Single(a => a.Id == article).PhotoIds.Should().BeEmpty();
    }
}
