using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Client.Core.Vision;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Devices.Camera;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Scenarios.Tests;

/// <summary>Stand-in for the local vision service: fixed answer, delay or failure; records what the register sends.</summary>
public sealed class FakeVisionClient : IVisionClient
{
    public VisionResponse Response { get; set; } = new("rec-0001", [], "mock", 120);

    public TimeSpan Delay { get; set; }

    public Exception? Failure { get; set; }

    public List<(IReadOnlyList<VisionCandidate> Candidates, IReadOnlyDictionary<string, IReadOnlyList<byte[]>> Photos)> Requests { get; } = [];

    public List<VisionFeedback> Feedbacks { get; } = [];

    public async Task<VisionResponse> RecognizeAsync(CapturedImage image, IReadOnlyList<VisionCandidate> candidates,
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> referencePhotos, string? registerId, CancellationToken ct)
    {
        Requests.Add((candidates, referencePhotos));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        return Failure is null ? Response : throw Failure;
    }

    public Task<bool> SendFeedbackAsync(VisionFeedback feedback, CancellationToken ct)
    {
        Feedbacks.Add(feedback);
        return Task.FromResult(true);
    }
}

[Collection(ScenarioCollection.Name)]
public sealed class VisionScenarios(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Cas1 = DemoDataSeeder.Id("register:CAS1");
    private TestDatabase _database = null!;
    private ApiFactory _api = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync(seedDemo: true);
        _api = new ApiFactory(_database.ConnectionString);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<T> GetAsync<T>(string url) => (await (await _api.Admin().GetAsync(url)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<T>())!;

    private Task<RegisterHarness> RegisterAsync(FakeVisionClient vision, Action<VisionOptions>? options = null)
    {
        var register = new RegisterOptions();
        options?.Invoke(register.Vision);
        return RegisterHarness.CreateAsync(_api, Cas1, register, configure: s => s.AddSingleton<IVisionClient>(vision));
    }

    private static async Task DrainAsync(RegisterHarness register)
    {
        for (var i = 0; i < 50 && await register.Get<LocalStore>().CountPendingAsync() > 0; i++)
        {
            await register.Get<SyncService>().RunOnceAsync();
        }

        (await register.Get<LocalStore>().CountPendingAsync()).Should().Be(0, register.Get<ConnectivityState>().Label);
    }

    private static VisionItem Item(string code, decimal confidence, string? alternative = null, decimal alternativeConfidence = 0.2m) =>
        new(code, confidence, [10, 10, 200, 200], alternative is null ? [] : [new VisionAlternative(alternative, alternativeConfidence)]);

    [Fact]
    public async Task Assisted_sale_applies_the_thresholds_and_reports_corrections()
    {
        var vision = new FakeVisionClient
        {
            Response = new VisionResponse("rec-assisted", [
                Item("CSC-VND", 0.96m, "CSC-PLT"),   // ≥ 0.90: added automatically
                Item("FRU-SAI", 0.72m, "YAO-NAT"),   // 0.60–0.90: highlighted, second choice one tap away
                Item("SOD-33", 0.41m),               // < 0.60: category only
            ], "gemini", 1800),
        };
        await using var register = await RegisterAsync(vision);
        await register.LoginAndOpenAsync();
        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();
        var stopwatch = Stopwatch.StartNew();

        await screen.CaptureTrayCommand.ExecuteAsync(null);                                     // 1. photo
        screen.Error.Should().BeNull();
        screen.Message.Should().StartWith("Plateau reconnu en").And.Contain("1 ligne(s) à vérifier").And.Contain("1 article(s) à choisir");
        var candidates = vision.Requests.Single().Candidates;
        candidates.Should().Contain(c => c.ArticleCode == "CSC-VND" && c.Category == "Plats chauds" && c.VisualDescription != null);
        candidates.Select(c => c.ArticleCode).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(screen.Groups.SelectMany(g => g.Items).Select(i => i.Item.ArticleCode), "only today's menu is proposed");

        screen.Cart.Lines.Should().HaveCount(2);
        var auto = screen.Cart.Lines[0];
        auto.Code.Should().Be("CSC-VND");
        auto.NeedsReview.Should().BeFalse();
        var review = screen.Cart.Lines[1];
        review.Code.Should().Be("FRU-SAI");
        review.NeedsReview.Should().BeTrue();
        review.HasAlternative.Should().BeTrue();
        review.ReviewText.Should().Be("Reconnu à 72 % — sinon " + review.Alternative!.Label + " ?");
        screen.Hints.Should().ContainSingle().Which.CategoryName.Should().Be("Boissons");

        screen.SwitchToAlternativeCommand.Execute(review);                                     // the yoghurt, not the fruit
        var water = screen.Groups.SelectMany(g => g.Items).Single(i => i.Item.ArticleCode == "EAU-50");
        screen.AddItemCommand.Execute(water);                                                  // the drink: water, not soda
        screen.Hints.Should().BeEmpty();
        screen.Cart.Lines.Select(l => (l.Code, l.Source.ToString())).Should().Equal(
            ("CSC-VND", "VisionAuto"), ("YAO-NAT", "VisionCorrected"), ("EAU-50", "VisionCorrected"));

        await screen.ScanBadgeCommand.ExecuteAsync("BDG-ATL0001");                             // 2. badge
        await screen.PayWithAccountCommand.ExecuteAsync(null);                                 // 3. pay
        stopwatch.Stop();
        screen.Error.Should().BeNull();
        screen.Message.Should().StartWith("Ticket CAS1-00000001");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "a tray is paid in less than 10 s in assisted mode");

        await screen.LastOutcome;
        var feedback = vision.Feedbacks.Should().ContainSingle().Subject;
        feedback.RecognitionId.Should().Be("rec-assisted");
        feedback.Lines.Should().BeEquivalentTo(new[]
        {
            new VisionFeedbackLine("CSC-VND", 1, "VisionAuto", 0),
            new VisionFeedbackLine("YAO-NAT", 1, "VisionCorrected", 1),
            new VisionFeedbackLine("EAU-50", 1, "VisionCorrected", 2),
        });

        await DrainAsync(register);
        var page = await GetAsync<PagedResult<TicketSummaryDto>>("/api/v1/tickets?search=CAS1-00000001");
        var ticket = await GetAsync<TicketDetailDto>($"/api/v1/tickets/{page.Items.Single().Id}");
        ticket.Lines.Select(l => l.Source).Should().BeEquivalentTo(["VisionAuto", "VisionCorrected", "VisionCorrected"]);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var stats = await GetAsync<VisionStatsDto>($"/api/v1/vision/stats?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}");
        stats.Total.Should().Match<VisionKpiDto>(k => k.Recognitions == 1 && k.TimedOut == 0 && k.Lines == 3 && k.Auto == 1 && k.Corrected == 2
                                                      && k.AverageLatencyMs == 1800);
        stats.Total.AutoAcceptRate.Should().Be(0.3333m);
        stats.Confusions.Should().BeEquivalentTo(new[] { new VisionConfusionDto("FRU-SAI", "YAO-NAT", 1), new VisionConfusionDto("SOD-33", "EAU-50", 1) });
        stats.BySite.Should().ContainSingle().Which.Label.Should().Contain("Sidi");
        stats.ByProvider.Should().ContainSingle().Which.Label.Should().Be("gemini");
    }

    [Fact]
    public async Task Slow_vision_service_falls_back_to_manual_entry_without_blocking_the_sale()
    {
        var vision = new FakeVisionClient { Delay = TimeSpan.FromSeconds(30), Response = new VisionResponse("late", [Item("CSC-VND", 0.99m)], "gemini", 30000) };
        await using var register = await RegisterAsync(vision, o => o.Timeout = TimeSpan.FromMilliseconds(400));
        await register.LoginAndOpenAsync();
        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();

        var stopwatch = Stopwatch.StartNew();
        await screen.CaptureTrayCommand.ExecuteAsync(null);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        screen.Error.Should().StartWith("Reconnaissance trop lente");
        screen.Cart.IsEmpty.Should().BeTrue();
        screen.IsRecognizing.Should().BeFalse();
        screen.CaptureTrayCommand.CanExecute(null).Should().BeTrue();

        screen.AddItemCommand.Execute(screen.Groups.SelectMany(g => g.Items).Single(i => i.Item.ArticleCode == "PAIN"));
        screen.Tendered = 2m;
        await screen.PayCashCommand.ExecuteAsync(null);
        screen.Message.Should().StartWith("Ticket CAS1-00000001");
        await screen.LastOutcome;
        vision.Feedbacks.Should().BeEmpty("the service gave no recognition id");

        await DrainAsync(register);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var stats = await GetAsync<VisionStatsDto>($"/api/v1/vision/stats?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}");
        stats.Total.Should().Match<VisionKpiDto>(k => k.Recognitions == 1 && k.TimedOut == 1 && k.Manual == 1);
    }

    [Fact]
    public async Task Vision_service_down_or_in_error_never_blocks_the_sale()
    {
        // Real HTTP client against a port where nothing listens.
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1, new RegisterOptions { Vision = { BaseUrl = new Uri("http://127.0.0.1:9/") } });
        await register.LoginAndOpenAsync();
        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();
        await screen.CaptureTrayCommand.ExecuteAsync(null);
        screen.Error.Should().Be("Service de reconnaissance injoignable. Saisir le plateau.");

        var failing = new FakeVisionClient { Failure = new InvalidOperationException("model crashed") };
        await using var other = await RegisterAsync(failing);
        await other.LoginAndOpenAsync();
        var screen2 = other.Get<SaleViewModel>();
        await screen2.LoadAsync();
        await screen2.CaptureTrayCommand.ExecuteAsync(null);
        screen2.Error.Should().Be("Reconnaissance indisponible : saisir le plateau.");
    }

    [Fact]
    public async Task Medium_confidence_line_can_be_confirmed_and_a_new_photo_replaces_the_previous_proposal()
    {
        var vision = new FakeVisionClient { Response = new VisionResponse("rec-1", [Item("CSC-PLT", 0.80m, "CSC-VND", 0.15m)], "mock", 100) };
        await using var register = await RegisterAsync(vision);
        await register.LoginAndOpenAsync();
        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();
        screen.AddItemCommand.Execute(screen.Groups.SelectMany(g => g.Items).Single(i => i.Item.ArticleCode == "PAIN"));

        await screen.CaptureTrayCommand.ExecuteAsync(null);
        await screen.CaptureTrayCommand.ExecuteAsync(null);                                    // photo retaken: proposals replaced, manual lines kept
        screen.Cart.Lines.Select(l => l.Code).Should().Equal("PAIN", "CSC-PLT");
        var line = screen.Cart.Lines[1];
        screen.ConfirmLineCommand.Execute(line);
        line.NeedsReview.Should().BeFalse();
        line.Source.ToString().Should().Be("VisionConfirmed");
        line.Quantity++;                                                                       // two plates, one seen

        await screen.ScanBadgeCommand.ExecuteAsync("BDG-ATL0002");
        await screen.PayWithAccountCommand.ExecuteAsync(null);
        screen.Error.Should().BeNull();
        await screen.LastOutcome;
        vision.Feedbacks.Single().Lines.Should().BeEquivalentTo(new[]
        {
            new VisionFeedbackLine("PAIN", 1, "Manual", null),
            new VisionFeedbackLine("CSC-PLT", 1, "VisionConfirmed", 0),
            new VisionFeedbackLine("CSC-PLT", 1, "Manual", null),
        });
        await DrainAsync(register);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var stats = await GetAsync<VisionStatsDto>($"/api/v1/vision/stats?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        stats.Total.Recognitions.Should().Be(2, "the replaced photo is logged as abandoned");
        stats.Total.Confirmed.Should().Be(1);
    }

    [Fact]
    public async Task Reference_photos_are_downloaded_once_and_sent_with_the_recognition()
    {
        var article = DemoDataSeeder.Id("article:CSC-VND");
        using (var form = new MultipartFormDataContent())
        {
            var photo = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9]);
            photo.Headers.ContentType = new("image/jpeg");
            form.Add(photo, "file", "couscous.jpg");
            (await _api.Admin().PostAsync($"/api/v1/articles/{article}/photos", form)).EnsureSuccessStatusCode();
        }

        var vision = new FakeVisionClient();
        await using var register = await RegisterAsync(vision);
        await register.LoginAndOpenAsync();
        var screen = register.Get<SaleViewModel>();
        await screen.LoadAsync();
        await register.Get<TrayRecognitionService>().PrefetchPhotosAsync(screen.Groups.SelectMany(g => g.Items));
        Directory.GetFiles(Path.Combine(register.Folder, "photos")).Should().ContainSingle();

        await screen.CaptureTrayCommand.ExecuteAsync(null);
        var photos = vision.Requests.Single().Photos;
        photos.Should().ContainKey("CSC-VND").WhoseValue.Should().ContainSingle().Which.Should().HaveCount(9);
        screen.Message.Should().Be("Aucun article reconnu : saisir le plateau.");
    }

    [Fact]
    public async Task Photo_added_in_the_back_office_reaches_the_register_by_incremental_sync()
    {
        await using var register = await RegisterAsync(new FakeVisionClient());
        await register.Get<SyncService>().RunOnceAsync();
        var article = DemoDataSeeder.Id("article:TAJ-PLT");
        (await register.Get<ReferenceCache>().GetArticlesAsync())[article].PhotoIds.Should().BeEmpty();

        using var form = new MultipartFormDataContent();
        var photo = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]);
        photo.Headers.ContentType = new("image/jpeg");
        form.Add(photo, "file", "tajine.jpg");
        (await _api.Admin().PostAsync($"/api/v1/articles/{article}/photos", form)).EnsureSuccessStatusCode();

        await register.Get<SyncService>().PullReferenceAsync();
        (await register.Get<ReferenceCache>().GetArticlesAsync())[article].PhotoIds.Should().ContainSingle();
    }
}

/// <summary>The HTTP client of the local vision service (wire format shared with vision/app/models.py).</summary>
public sealed class VisionClientTests
{
    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return answer(request);
        }
    }

    private static readonly VisionOptions Options = new() { BaseUrl = new Uri("http://127.0.0.1:8765/") };

    [Fact]
    public async Task Recognize_sends_multipart_snake_case_and_reads_the_answer()
    {
        var answer = """
            {"recognition_id":"abc123","items":[{"article_code":"CSC-VND","confidence":0.93,"bbox":[1,2,30,40],"bbox_yolo":[0.1,0.1,0.2,0.2],
              "alternatives":[{"article_code":"CSC-PLT","confidence":0.05}]}],"provider":"gemini","latency_ms":1234,"image_width":1600,
              "image_height":1200,"rejected_codes":[]}
            """;
        var handler = new Recorder(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, System.Text.Encoding.UTF8, "application/json") });
        var client = new VisionClient(new HttpClient(handler), Options);

        var response = await client.RecognizeAsync(new CapturedImage([1, 2, 3], 10, 10, DateTimeOffset.UtcNow),
            [new VisionCandidate("CSC-VND", "Couscous viande", "Plats chauds", null)],
            new Dictionary<string, IReadOnlyList<byte[]>> { ["CSC-VND"] = [[9, 9]] }, "REG-1", default);

        handler.Request!.RequestUri.Should().Be(new Uri("http://127.0.0.1:8765/recognize"));
        handler.Body.Should().Contain("name=image").And.Contain("name=candidates").And.Contain("name=reference_CSC-VND").And.Contain("name=register_id")
            .And.Contain("""[{"article_code":"CSC-VND","label":"Couscous viande","category":"Plats chauds"}]""");
        response.RecognitionId.Should().Be("abc123");
        response.LatencyMs.Should().Be(1234);
        response.Items.Single().Should().Match<VisionItem>(i => i.Confidence == 0.93m && i.Alternatives!.Single().ArticleCode == "CSC-PLT");
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout, """{"detail":{"code":"timeout","message":"x"}}""", "Reconnaissance trop lente.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"detail":{"code":"provider_unavailable","message":"x"}}""", "Reconnaissance non configurée sur ce poste.")]
    [InlineData(HttpStatusCode.BadGateway, "oops", "Reconnaissance indisponible (HTTP 502).")]
    public async Task Service_errors_become_a_message_for_the_cashier(HttpStatusCode status, string body, string message)
    {
        var client = new VisionClient(new HttpClient(new Recorder(_ => new HttpResponseMessage(status) { Content = new StringContent(body) })), Options);
        var act = () => client.RecognizeAsync(new CapturedImage([1], 1, 1, DateTimeOffset.UtcNow), [], new Dictionary<string, IReadOnlyList<byte[]>>(), null, default);
        (await act.Should().ThrowAsync<VisionUnavailableException>()).Which.Message.Should().Be(message);
    }

    [Fact]
    public async Task Feedback_is_posted_in_snake_case_and_failures_are_swallowed()
    {
        var handler = new Recorder(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var ok = await new VisionClient(new HttpClient(handler), Options)
            .SendFeedbackAsync(new VisionFeedback("abc123", "t-1", [new VisionFeedbackLine("PAIN", 2, "Manual", null)]), default);
        ok.Should().BeTrue();
        using var json = JsonDocument.Parse(handler.Body!);
        json.RootElement.GetProperty("recognition_id").GetString().Should().Be("abc123");
        json.RootElement.GetProperty("lines")[0].GetProperty("article_code").GetString().Should().Be("PAIN");
        json.RootElement.GetProperty("lines")[0].TryGetProperty("prediction_index", out _).Should().BeFalse();

        var down = new VisionClient(new HttpClient(new Recorder(_ => throw new HttpRequestException("refused"))), Options);
        (await down.SendFeedbackAsync(new VisionFeedback("abc123", null, []), default)).Should().BeFalse();
    }

    [Fact]
    public async Task Camera_is_read_through_the_vision_service()
    {
        var handler = new Recorder(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]) };
            response.Headers.Add("X-Image-Width", "1000");
            response.Headers.Add("X-Image-Height", "700");
            return response;
        });
        var image = await new VisionServiceCamera(new HttpClient(handler), Options, TimeProvider.System).CaptureAsync();
        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/camera/capture");
        (image.Width, image.Height, image.Jpeg.Length).Should().Be((1000, 700, 4));

        var broken = new VisionServiceCamera(new HttpClient(new Recorder(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))), Options,
            TimeProvider.System);
        await broken.Invoking(c => c.CaptureAsync()).Should().ThrowAsync<VisionUnavailableException>().WithMessage("Caméra indisponible.");
        broken.Status.Should().Be(Newrest.Pos.Devices.DeviceStatus.Error);
    }
}
