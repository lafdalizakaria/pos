using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Domain.Tests;

internal static class TestData
{
    public static readonly Guid RegisterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid OperatorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid DinerId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid AccountId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid CouscousViande = Guid.Parse("66666666-6666-6666-6666-666666666601");
    public static readonly Guid Eau = Guid.Parse("66666666-6666-6666-6666-666666666602");
    public static readonly DateOnly Today = new(2026, 10, 5);
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 15, 30, 123, TimeSpan.FromHours(1));

    public static TicketLineInput Couscous(int qty = 1) => new(CouscousViande, "CSC-VND", "Couscous viande", qty, 40.00m, 0.10m);

    public static TicketLineInput Water(int qty = 1) => new(Eau, "EAU-50", "Eau minérale 50cl", qty, 5.00m, 0.20m, IsSubsidizable: false);

    public static TicketIssueRequest Sale(long sequence, string previousHash, params PaymentInput[] payments) => new()
    {
        Id = Guid.CreateVersion7(),
        RegisterId = RegisterId,
        RegisterPrefix = "C01",
        Sequence = sequence,
        PreviousHash = previousHash,
        CashSessionId = SessionId,
        OperatorId = OperatorId,
        BusinessDate = Today,
        IssuedAt = Now.AddMinutes(sequence),
        Lines = [Couscous(), Water()],
        Payments = payments.Length > 0 ? payments : [new PaymentInput(PaymentMethod.Cash, 45.00m) { Tendered = 50m }],
    };

    public static List<Ticket> Chain(int count)
    {
        var tickets = new List<Ticket>();
        var previous = TicketHasher.GenesisHash;
        for (var i = 1; i <= count; i++)
        {
            var ticket = Ticket.Issue(Sale(i, previous));
            tickets.Add(ticket);
            previous = ticket.Hash;
        }

        return tickets;
    }
}
