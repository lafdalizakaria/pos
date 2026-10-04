using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Vision;

namespace Newrest.Pos.Domain.Tests;

public class RecognitionPolicyTests
{
    [Theory]
    [InlineData(1.00, RecognitionDecision.AutoAccept)]
    [InlineData(0.90, RecognitionDecision.AutoAccept)]
    [InlineData(0.8999, RecognitionDecision.NeedsConfirmation)]
    [InlineData(0.60, RecognitionDecision.NeedsConfirmation)]
    [InlineData(0.5999, RecognitionDecision.ManualSelection)]
    [InlineData(0.00, RecognitionDecision.ManualSelection)]
    public void Default_thresholds(decimal confidence, RecognitionDecision expected) =>
        new RecognitionConfidencePolicy().Classify(confidence).Should().Be(expected);

    [Fact]
    public void Custom_thresholds_and_validation()
    {
        var policy = new RecognitionConfidencePolicy(0.5m, 0.5m);
        policy.Classify(0.5m).Should().Be(RecognitionDecision.AutoAccept);
        policy.Classify(0.49m).Should().Be(RecognitionDecision.ManualSelection);

        FluentActions.Invoking(() => new RecognitionConfidencePolicy(0.9m, 0.6m)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => new RecognitionConfidencePolicy(-0.1m, 0.6m)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => new RecognitionConfidencePolicy(0.1m, 1.1m)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => policy.Classify(1.01m)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Recognition_log_records_feedback()
    {
        var log = new RecognitionLog(Guid.CreateVersion7(), TestData.RegisterId, TestData.Now, "gemini", 2140, "[]");
        var ticketId = Guid.CreateVersion7();

        log.RecordFeedback(ticketId, "[{\"code\":\"CSC-VND\"}]", 3, 2, 1, 0);

        log.TicketId.Should().Be(ticketId);
        log.CorrectedCount.Should().Be(1);
        FluentActions.Invoking(() => log.RecordFeedback(ticketId, "[]", -1, 0, 0, 0)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => new RecognitionLog(Guid.CreateVersion7(), TestData.RegisterId, TestData.Now, "mock", -1, "[]"))
            .Should().Throw<DomainException>();
    }
}
