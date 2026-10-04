using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Vision;

namespace Newrest.Pos.Domain.Tests;

public class VisionDeploymentTests
{
    private static readonly string Sha = new('a', 64);

    private static VisionModel Model(string version = "20261003-1200") =>
        new(Guid.NewGuid(), version, ["csc-vnd", "EAU-50"], 640, Sha, 1000, "vision-models/x/model.onnx", 0.91m, "{}", "admin@newrest.ma");

    [Fact]
    public void Model_is_a_draft_until_published_and_cannot_come_back_once_retired()
    {
        var model = Model();
        model.Classes.Should().Equal("CSC-VND", "EAU-50");
        model.ClassCount.Should().Be(2);
        model.Status.Should().Be(VisionModelStatus.Draft);
        model.Publish(TestData.Now);
        model.Status.Should().Be(VisionModelStatus.Published);
        model.PublishedAt.Should().Be(TestData.Now);
        model.Retire();
        model.Invoking(m => m.Publish(TestData.Now)).Should().Throw<DomainException>().Which.Code.Should().Be("model_retired");
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("")]
    [InlineData("a-version-name-that-is-far-too-long-x")]
    public void Model_version_is_a_safe_short_name(string version) =>
        FluentActions.Invoking(() => Model(version)).Should().Throw<DomainException>().Which.Code.Should().Be("invalid_version");

    [Fact]
    public void Model_rejects_duplicate_classes_bad_hash_and_metrics()
    {
        FluentActions.Invoking(() => new VisionModel(Guid.NewGuid(), "v1", ["A", "a"], 640, Sha, 1, "p", null, "{}", "u"))
            .Should().Throw<DomainException>().Which.Code.Should().Be("invalid_model");
        FluentActions.Invoking(() => new VisionModel(Guid.NewGuid(), "v1", ["A"], 640, "XYZ", 1, "p", null, "{}", "u"))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => new VisionModel(Guid.NewGuid(), "v1", ["A"], 640, Sha, 1, "p", 1.5m, "{}", "u"))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => new VisionModel(Guid.NewGuid(), "v1", ["A"], 10, Sha, 1, "p", null, "{}", "u"))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Site_settings_validate_thresholds_and_keep_a_model_only_for_local_providers()
    {
        var settings = new SiteVisionSettings(Guid.NewGuid(), Guid.NewGuid());
        settings.Provider.Should().Be(VisionProvider.Gemini);
        (settings.LowThreshold, settings.HighThreshold).Should().Be((0.60m, 0.90m));

        var model = Guid.NewGuid();
        settings.Update(true, VisionProvider.Hybrid, model, 0.55m, 0.85m, 0.7m);
        settings.ModelId.Should().Be(model);
        settings.Update(true, VisionProvider.Gemini, model, 0.55m, 0.85m, 0.7m);
        settings.ModelId.Should().BeNull("Gemini uses no local model");

        settings.Invoking(s => s.Update(true, VisionProvider.Yolo, null, 0.9m, 0.8m, 0.6m)).Should().Throw<DomainException>()
            .Which.Code.Should().Be("invalid_thresholds");
        settings.Invoking(s => s.Update(true, VisionProvider.Yolo, null, 0.5m, 0.8m, 1.2m)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Register_status_is_bounded()
    {
        var status = new RegisterVisionStatus(Guid.NewGuid());
        status.Report("yolo", "v1", true, true, new string('x', 900), TestData.Now);
        status.Error.Should().HaveLength(500);
        status.ReportedAt.Should().Be(TestData.Now);
        status.RegisterId.Should().Be(status.Id);
        status.Invoking(s => s.Report(new string('p', 40), null, true, false, null, TestData.Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Advisor_suggests_thresholds_from_the_cashiers_validations()
    {
        var outcomes = new List<PredictionOutcome>();
        // High confidence: 99 % right; 0.70-0.85: 75 % right; below 0.5: mostly wrong.
        for (var i = 0; i < 100; i++)
        {
            outcomes.Add(new PredictionOutcome(0.92m, i != 0));
            outcomes.Add(new PredictionOutcome(0.86m + (i % 3) / 100m, i % 50 != 0));
            outcomes.Add(new PredictionOutcome(0.70m + (i % 15) / 100m, i % 4 != 0));
            outcomes.Add(new PredictionOutcome(0.40m, i % 5 == 0));
        }

        var advice = ThresholdAdvisor.Advise(outcomes);
        advice.Predictions.Should().Be(400);
        advice.SuggestedHigh.Should().BeInRange(0.80m, 0.86m, "auto-add only where the model is right 97 % of the time");
        advice.PrecisionAtHigh.Should().BeGreaterThanOrEqualTo(0.97m);
        advice.SuggestedLow.Should().Be(0.70m);
        advice.PrecisionAtLow.Should().BeApproximately(0.75m, 0.02m);
        advice.Buckets.Should().HaveCount(10);
        advice.Buckets[9].Should().Be(new ConfidenceBucket(0.9m, 1.0m, 100, 99));
        advice.Buckets[4].Precision.Should().Be(0.2m);
    }

    [Fact]
    public void Advisor_needs_enough_data()
    {
        var advice = ThresholdAdvisor.Advise([new PredictionOutcome(0.99m, true), new PredictionOutcome(1m, true)]);
        advice.SuggestedHigh.Should().BeNull();
        advice.SuggestedLow.Should().BeNull();
        advice.Buckets[9].Count.Should().Be(2, "a confidence of exactly 1 belongs to the last bucket");
    }
}
