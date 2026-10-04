using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Domain.Tests;

public class TicketNumberTests
{
    [Theory]
    [InlineData("C01", 1, "C01-00000001")]
    [InlineData("csm01s1", 42, "CSM01S1-00000042")]
    [InlineData("TNG2", 12345678, "TNG2-12345678")]
    public void Format_pads_the_sequence(string prefix, long sequence, string expected) =>
        TicketNumber.Format(prefix, sequence).Should().Be(expected);

    [Theory]
    [InlineData("C")]
    [InlineData("C-01")]
    [InlineData("TOOLONGPREFIX1")]
    [InlineData(" ")]
    public void Invalid_prefixes_are_rejected(string prefix) =>
        FluentActions.Invoking(() => TicketNumber.ValidatePrefix(prefix)).Should().Throw<DomainException>();

    [Fact]
    public void Next_increments_and_rejects_negative_or_zero()
    {
        TicketNumber.Next(0).Should().Be(1);
        TicketNumber.Next(41).Should().Be(42);
        FluentActions.Invoking(() => TicketNumber.Next(-1)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => TicketNumber.Format("C01", 0)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => TicketNumber.Next(long.MaxValue)).Should().Throw<OverflowException>();
    }

    [Fact]
    public void Gaps_are_found()
    {
        TicketNumber.FindGaps([1, 2, 3, 4]).Should().BeEmpty();
        TicketNumber.FindGaps([1, 2, 5, 7]).Should().Equal(3, 4, 6);
        TicketNumber.FindGaps([3, 4], firstExpected: 1).Should().Equal(1, 2);
        TicketNumber.FindGaps([10, 12], firstExpected: 10).Should().Equal(11);
        TicketNumber.FindGaps([]).Should().BeEmpty();
    }
}
