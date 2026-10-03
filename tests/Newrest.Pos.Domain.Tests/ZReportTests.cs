using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Domain.Tests;

public class ZReportTests
{
    [Fact]
    public void Z_aggregates_payments_vat_subsidies_top_ups_and_cash_difference()
    {
        var session = new CashSession(TestData.SessionId, TestData.RegisterId, TestData.OperatorId, 500m, TestData.Now.AddHours(-2), TestData.Today);
        var t1 = Ticket.Issue(TestData.Sale(1, TicketHasher.GenesisHash)); // 45 cash
        var t2 = Ticket.Issue(TestData.Sale(2, t1.Hash) with
        {
            DinerId = TestData.DinerId,
            AccountId = TestData.AccountId,
            SubsidyAmount = 20m,
            Payments = [new PaymentInput(PaymentMethod.Account, 15m), new PaymentInput(PaymentMethod.Card, 10m) { AuthorizationCode = "X1" }],
        });
        var credit = Ticket.IssueCreditNote(t1, new CreditNoteRequest
        {
            Id = Guid.CreateVersion7(),
            RegisterPrefix = "C01",
            Sequence = 3,
            PreviousHash = t2.Hash,
            CashSessionId = session.Id,
            OperatorId = TestData.OperatorId,
            BusinessDate = TestData.Today,
            IssuedAt = TestData.Now,
            Reason = "Retour",
        });
        var account = new Account(TestData.AccountId, TestData.DinerId, Guid.CreateVersion7(), AccountType.Prepaid);
        var topUpCash = account.Post(new MovementRequest(MovementType.TopUp, 100m, Guid.CreateVersion7(), TestData.Now)
        { RegisterId = TestData.RegisterId, PaymentMethod = PaymentMethod.Cash });
        var topUpCard = account.Post(new MovementRequest(MovementType.TopUp, 50m, Guid.CreateVersion7(), TestData.Now)
        { RegisterId = TestData.RegisterId, PaymentMethod = PaymentMethod.Card });
        var consumption = account.Post(new MovementRequest(MovementType.Consumption, -15m, Guid.CreateVersion7(), TestData.Now)
        { RegisterId = TestData.RegisterId });
        var otherRegister = account.Post(new MovementRequest(MovementType.TopUp, 70m, Guid.CreateVersion7(), TestData.Now)
        { RegisterId = Guid.CreateVersion7(), PaymentMethod = PaymentMethod.Cash });

        var z = ZReportCalculator.Compute(new ZReportRequest(Guid.CreateVersion7(), session, 7, TestData.Now.AddHours(3), 598m,
            [t1, t2, credit], [topUpCash, topUpCard, consumption, otherRegister]));

        z.ZNumber.Should().Be(7);
        z.SaleCount.Should().Be(2);
        z.CreditNoteCount.Should().Be(1);
        z.FirstTicketSequence.Should().Be(1);
        z.LastTicketSequence.Should().Be(3);
        z.LastTicketHash.Should().Be(credit.Hash);
        z.GrossSales.Should().Be(90m);
        z.CreditNotesTotal.Should().Be(-45m);
        z.NetSales.Should().Be(45m);
        z.TotalVat.Should().Be(t2.TotalVat);
        z.SubsidyTotal.Should().Be(20m);
        z.DinerShareTotal.Should().Be(25m);
        z.AccountTopUpTotal.Should().Be(150m);
        // 500 float + 45 cash sale - 45 cash refund + 100 cash top-up
        z.ExpectedCash.Should().Be(600m);
        z.CashDifference.Should().Be(-2m);

        z.Lines.Where(l => l.Section == ZSection.Payment).Select(l => (l.Key, l.Amount))
            .Should().BeEquivalentTo(new[] { ("Cash", 0m), ("Card", 10m), ("Account", 15m) });
        z.Lines.Where(l => l.Section == ZSection.AccountTopUp).Select(l => (l.Key, l.Amount))
            .Should().BeEquivalentTo(new[] { ("Cash", 100m), ("Card", 50m) });
        var vat10 = z.Lines.Single(l => l.Section == ZSection.Vat && l.Key == "0.10");
        vat10.Amount.Should().Be(40m);
        vat10.TaxAmount.Should().Be(3.64m);
        vat10.BaseAmount.Should().Be(36.36m);

        session.Close(z, TestData.OperatorId);
        session.Status.Should().Be(CashSessionStatus.Closed);
        session.ZReportId.Should().Be(z.Id);
        FluentActions.Invoking(() => session.Close(z, TestData.OperatorId)).Should().Throw<DomainException>();
        FluentActions.Invoking(session.EnsureOpen).Should().Throw<DomainException>();
    }

    [Fact]
    public void Empty_session_and_guards()
    {
        var session = new CashSession(Guid.CreateVersion7(), TestData.RegisterId, TestData.OperatorId, 200m, TestData.Now, TestData.Today);

        var z = ZReportCalculator.Compute(new ZReportRequest(Guid.CreateVersion7(), session, 1, TestData.Now, 200m, [], []));

        z.ExpectedCash.Should().Be(200m);
        z.CashDifference.Should().Be(0m);
        z.FirstTicketSequence.Should().BeNull();

        var foreign = Ticket.Issue(TestData.Sale(1, TicketHasher.GenesisHash));
        FluentActions.Invoking(() => ZReportCalculator.Compute(new ZReportRequest(Guid.CreateVersion7(), session, 1, TestData.Now, 0m, [foreign], [])))
            .Should().Throw<DomainException>().Which.Code.Should().Be("z_foreign_ticket");
        FluentActions.Invoking(() => ZReportCalculator.Compute(new ZReportRequest(Guid.CreateVersion7(), session, 0, TestData.Now, 0m, [], [])))
            .Should().Throw<DomainException>();
        var otherSession = new CashSession(Guid.CreateVersion7(), TestData.RegisterId, TestData.OperatorId, 0m, TestData.Now, TestData.Today);
        FluentActions.Invoking(() => otherSession.Close(z, TestData.OperatorId)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => new CashSession(Guid.CreateVersion7(), TestData.RegisterId, TestData.OperatorId, -1m, TestData.Now, TestData.Today))
            .Should().Throw<DomainException>();
    }
}
