using System.Reflection;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Domain.Tests;

public class TicketChainTests
{
    [Fact]
    public void Hash_is_deterministic_hex_sha256()
    {
        var ticket = TestData.Chain(1)[0];

        ticket.Hash.Should().MatchRegex("^[0-9a-f]{64}$");
        TicketHasher.ComputeHash(ticket).Should().Be(ticket.Hash);
        ticket.PreviousHash.Should().Be(TicketHasher.GenesisHash);
        TicketHasher.Canonicalize(ticket).Should().Contain("\"Total\":\"45.00\"").And.Contain("\"Rate\":\"0.1000\"");
    }

    [Fact]
    public void Valid_chain_is_verified()
    {
        var chain = TestData.Chain(10);

        var result = TicketChainVerifier.Verify(TestData.RegisterId, chain);

        result.IsValid.Should().BeTrue();
        result.TicketsChecked.Should().Be(10);
        chain.Zip(chain.Skip(1)).Should().OnlyContain(pair => pair.Second.PreviousHash == pair.First.Hash);
    }

    [Fact]
    public void Altered_amount_is_detected()
    {
        var chain = TestData.Chain(5);
        SetPrivate(chain[2], nameof(Ticket.TotalAmount), 1.00m);

        var result = TicketChainVerifier.Verify(TestData.RegisterId, chain);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Kind == ChainIssueKind.HashMismatch && i.Sequence == 3);
    }

    [Fact]
    public void Altered_line_is_detected()
    {
        var chain = TestData.Chain(3);
        SetPrivate(chain[1].Lines[0], nameof(TicketLine.Label), "Couscous poulet");

        TicketChainVerifier.Verify(TestData.RegisterId, chain).Issues
            .Should().ContainSingle(i => i.Kind == ChainIssueKind.HashMismatch && i.Sequence == 2);
    }

    [Fact]
    public void Re_hashed_forgery_breaks_the_link_of_the_next_ticket()
    {
        var chain = TestData.Chain(4);
        SetPrivate(chain[1], nameof(Ticket.TotalAmount), 1.00m);
        SetPrivate(chain[1], nameof(Ticket.Hash), TicketHasher.ComputeHash(chain[1]));

        var result = TicketChainVerifier.Verify(TestData.RegisterId, chain);

        result.Issues.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = ChainIssueKind.BrokenLink, Sequence = 3L });
    }

    [Fact]
    public void Deleted_ticket_is_detected_as_gap_and_broken_link()
    {
        var chain = TestData.Chain(5);
        chain.RemoveAt(2);

        var result = TicketChainVerifier.Verify(TestData.RegisterId, chain);

        result.Issues.Select(i => (i.Kind, i.Sequence)).Should().BeEquivalentTo(new[]
        {
            (ChainIssueKind.Gap, 3L),
            (ChainIssueKind.BrokenLink, 4L),
        });
    }

    [Fact]
    public void Duplicates_and_foreign_tickets_are_reported()
    {
        var chain = TestData.Chain(3);
        var foreign = TestData.Chain(1)[0];
        SetPrivate(foreign, nameof(Ticket.RegisterId), Guid.CreateVersion7());

        var result = TicketChainVerifier.Verify(TestData.RegisterId, [.. chain, chain[1], foreign]);

        result.Issues.Should().Contain(i => i.Kind == ChainIssueKind.Duplicate && i.Sequence == 2);
        result.Issues.Should().Contain(i => i.Kind == ChainIssueKind.ForeignRegister);
    }

    [Fact]
    public void Verification_can_start_from_a_checkpoint()
    {
        var chain = TestData.Chain(6);

        TicketChainVerifier.Verify(TestData.RegisterId, chain.Skip(3), 4, chain[2].Hash).IsValid.Should().BeTrue();
        TicketChainVerifier.Verify(TestData.RegisterId, chain.Skip(3), 4, TicketHasher.GenesisHash).IsValid.Should().BeFalse();
        TicketChainVerifier.Verify(TestData.RegisterId, chain.Skip(3), 4).IsValid.Should().BeTrue();
    }

    private static void SetPrivate(object target, string property, object value) =>
        target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!
            .SetMethod!.Invoke(target, [value]);
}
