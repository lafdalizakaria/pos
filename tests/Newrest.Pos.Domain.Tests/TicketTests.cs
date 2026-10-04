using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Domain.Tests;

public class TicketTests
{
    [Fact]
    public void Sale_computes_totals_vat_and_change()
    {
        var ticket = Ticket.Issue(TestData.Sale(1, TicketHasher.GenesisHash));

        ticket.Number.Should().Be("C01-00000001");
        ticket.TotalAmount.Should().Be(45.00m);
        ticket.TotalVat.Should().Be(3.64m + 0.83m); // 40 * 0.1/1.1 = 3.636 ; 5 * 0.2/1.2 = 0.833
        ticket.DinerShare.Should().Be(45m);
        ticket.Lines.Select(l => l.LineNumber).Should().Equal(1, 2);
        ticket.Payments.Single().Change.Should().Be(5m);
        ticket.IssuedAt.Offset.Should().Be(TimeSpan.Zero);
        ticket.IssuedAt.Millisecond.Should().Be(123);
    }

    [Fact]
    public void Subsidized_sale_with_mixed_payments()
    {
        var request = TestData.Sale(1, TicketHasher.GenesisHash) with
        {
            DinerId = TestData.DinerId,
            AccountId = TestData.AccountId,
            SubsidyAmount = 20m,
            Payments =
            [
                new PaymentInput(PaymentMethod.Account, 15m) { AccountMovementId = Guid.CreateVersion7() },
                new PaymentInput(PaymentMethod.Card, 10m) { AuthorizationCode = " A12345 " },
            ],
        };

        var ticket = Ticket.Issue(request);

        ticket.SubsidyAmount.Should().Be(20m);
        ticket.DinerShare.Should().Be(25m);
        ticket.Payments.Should().HaveCount(2);
        ticket.Payments[1].AuthorizationCode.Should().Be("A12345");
    }

    [Fact]
    public void Fully_subsidized_ticket_needs_no_payment()
    {
        var request = TestData.Sale(1, TicketHasher.GenesisHash) with { DinerId = TestData.DinerId, SubsidyAmount = 45m, Payments = [] };

        Ticket.Issue(request).DinerShare.Should().Be(0m);
    }

    [Theory]
    [InlineData("payment_mismatch")]
    [InlineData("empty_ticket")]
    [InlineData("invalid_quantity")]
    [InlineData("subsidy_exceeds_total")]
    [InlineData("subsidy_without_diner")]
    [InlineData("insufficient_cash")]
    [InlineData("authorization_required")]
    [InlineData("account_required")]
    [InlineData("invalid_payment")]
    [InlineData("invalid_hash")]
    [InlineData("invalid_sequence")]
    [InlineData("unsupported_payment")]
    public void Invalid_sales_are_rejected(string expectedCode)
    {
        var baseRequest = TestData.Sale(1, TicketHasher.GenesisHash);
        var request = expectedCode switch
        {
            "payment_mismatch" => baseRequest with { Payments = [new PaymentInput(PaymentMethod.Cash, 40m)] },
            "empty_ticket" => baseRequest with { Lines = [] },
            "invalid_quantity" => baseRequest with { Lines = [TestData.Couscous(0)] },
            "subsidy_exceeds_total" => baseRequest with { DinerId = TestData.DinerId, SubsidyAmount = 46m },
            "subsidy_without_diner" => baseRequest with { SubsidyAmount = 5m, Payments = [new PaymentInput(PaymentMethod.Cash, 40m)] },
            "insufficient_cash" => baseRequest with { Payments = [new PaymentInput(PaymentMethod.Cash, 45m) { Tendered = 40m }] },
            "authorization_required" => baseRequest with { Payments = [new PaymentInput(PaymentMethod.Card, 45m)] },
            "account_required" => baseRequest with { Payments = [new PaymentInput(PaymentMethod.Account, 45m)] },
            "invalid_payment" => baseRequest with { Payments = [new PaymentInput(PaymentMethod.Cash, 50m), new PaymentInput(PaymentMethod.Cash, -5m)] },
            "invalid_hash" => baseRequest with { PreviousHash = "abc" },
            "invalid_sequence" => baseRequest with { Sequence = 0 },
            "unsupported_payment" => baseRequest with { Payments = [new PaymentInput(PaymentMethod.BankTransfer, 45m)] },
            _ => throw new InvalidOperationException(),
        };

        var act = () => Ticket.Issue(request);

        act.Should().Throw<DomainException>().Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void Credit_note_negates_the_original_and_continues_the_chain()
    {
        var original = Ticket.Issue(TestData.Sale(1, TicketHasher.GenesisHash) with
        {
            DinerId = TestData.DinerId,
            AccountId = TestData.AccountId,
            SubsidyAmount = 20m,
            Payments = [new PaymentInput(PaymentMethod.Account, 25m)],
        });

        var credit = Ticket.IssueCreditNote(original, new CreditNoteRequest
        {
            Id = Guid.CreateVersion7(),
            RegisterPrefix = "C01",
            Sequence = 2,
            PreviousHash = original.Hash,
            CashSessionId = TestData.SessionId,
            OperatorId = TestData.OperatorId,
            BusinessDate = TestData.Today,
            IssuedAt = TestData.Now.AddMinutes(5),
            Reason = "Plat refusé",
        });

        credit.Kind.Should().Be(TicketKind.CreditNote);
        credit.CreditedTicketId.Should().Be(original.Id);
        credit.TotalAmount.Should().Be(-45m);
        credit.TotalVat.Should().Be(-original.TotalVat);
        credit.SubsidyAmount.Should().Be(-20m);
        credit.DinerShare.Should().Be(-25m);
        credit.Payments.Single().Should().BeEquivalentTo(new { Method = PaymentMethod.Account, Amount = -25m });
        TicketChainVerifier.Verify(TestData.RegisterId, [original, credit]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Credit_note_with_custom_refund_and_guards()
    {
        var original = Ticket.Issue(TestData.Sale(1, TicketHasher.GenesisHash));
        var request = new CreditNoteRequest
        {
            Id = Guid.CreateVersion7(),
            RegisterPrefix = "C01",
            Sequence = 2,
            PreviousHash = original.Hash,
            CashSessionId = TestData.SessionId,
            OperatorId = TestData.OperatorId,
            BusinessDate = TestData.Today,
            IssuedAt = TestData.Now,
            Reason = "Erreur",
            RefundPayments = [new PaymentInput(PaymentMethod.Cash, -45m)],
        };

        var credit = Ticket.IssueCreditNote(original, request);
        credit.Payments.Single().Change.Should().BeNull();

        FluentActions.Invoking(() => Ticket.IssueCreditNote(credit, request with { Sequence = 3 }))
            .Should().Throw<DomainException>().Which.Code.Should().Be("cannot_credit_credit_note");
        FluentActions.Invoking(() => Ticket.IssueCreditNote(original, request with { RefundPayments = [new PaymentInput(PaymentMethod.Cash, 45m)] }))
            .Should().Throw<DomainException>().Which.Code.Should().Be("invalid_payment");
        FluentActions.Invoking(() => Ticket.IssueCreditNote(original, request with { Reason = " " }))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Eligible_amount_excludes_non_subsidizable_lines()
    {
        TicketLineInput[] lines = [TestData.Couscous(2), TestData.Water(3)];

        Ticket.ComputeTotal(lines).Should().Be(95m);
        Ticket.EligibleAmount(lines).Should().Be(80m);
    }
}
