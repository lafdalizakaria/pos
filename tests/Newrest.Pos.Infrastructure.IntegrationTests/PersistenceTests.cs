using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Menus;
using Newrest.Pos.Domain.Sales;
using Newrest.Pos.Infrastructure.Persistence.Interceptors;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Infrastructure.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PersistenceTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid RegisterId = DemoDataSeeder.Id("register:CAS1");
    private static readonly Guid OperatorId = DemoDataSeeder.Id("operator:NFMS:CAIS01");
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await fixture.CreateDatabaseAsync(seedDemo: true);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Demo_data_is_complete_and_seeding_is_idempotent()
    {
        await using var db = _database.CreateContext();

        (await db.Companies.CountAsync()).Should().Be(2);
        (await db.Sites.CountAsync()).Should().Be(3);
        (await db.PointsOfSale.CountAsync()).Should().Be(5);
        (await db.Articles.CountAsync()).Should().Be(30);
        (await db.Articles.Select(a => a.Code).ToListAsync()).Should().Contain(["CSC-VND", "CSC-PLT"]);
        (await db.DailyMenus.CountAsync(m => m.IsPublished)).Should().Be(5);
        (await db.Badges.CountAsync()).Should().Be(8);
        (await db.Contracts.Include(c => c.SubsidyRules).Include(c => c.PointsOfSale).SingleAsync(c => c.Reference == "CTR-ATLAS-2026"))
            .Should().Match<Domain.Clients.Contract>(c => c.SubsidyRules.Count == 2 && c.PointsOfSale.Count == 4);

        (await _database.CreateSeeder(db).SeedAsync()).Should().BeFalse();
        (await db.Articles.CountAsync()).Should().Be(30);
    }

    [Fact]
    public async Task Price_overrides_are_applied_to_menus()
    {
        await using var db = _database.CreateContext();
        var coffee = await db.Articles.SingleAsync(a => a.Code == "CAF-EXP");
        var snackMenu = await db.DailyMenus.Include(m => m.Items).SingleAsync(m => m.PointOfSaleId == DemoDataSeeder.Id("pos:TNG-SNACK"));
        var kenMenu = await db.DailyMenus.Include(m => m.Items).SingleAsync(m => m.PointOfSaleId == DemoDataSeeder.Id("pos:KEN-SELF"));
        var couscous = await db.Articles.SingleAsync(a => a.Code == "CSC-VND");

        snackMenu.Items.Single(i => i.ArticleId == coffee.Id).EffectivePrice.Should().Be(6.00m);
        kenMenu.Items.Single(i => i.ArticleId == couscous.Id).EffectivePrice.Should().Be(40.00m);
        couscous.BasePrice.Should().Be(42.00m);
    }

    [Fact]
    public async Task Tickets_round_trip_with_a_valid_hash_chain_and_are_immutable()
    {
        var session = new CashSession(Guid.NewGuid(), RegisterId, OperatorId, 300m, DateTimeOffset.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow));
        var tickets = await SeedTicketsAsync(session, 5);

        await using (var db = _database.CreateContext())
        {
            var stored = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments)
                .Where(t => t.RegisterId == RegisterId).ToListAsync();

            stored.Should().HaveCount(5);
            stored.Should().OnlyContain(t => t.HasValidHash());
            TicketChainVerifier.Verify(RegisterId, stored).IsValid.Should().BeTrue();
            stored.Select(t => t.Hash).Should().BeEquivalentTo(tickets.Select(t => t.Hash));
        }

        await using (var db = _database.CreateContext())
        {
            // Direct tampering in SQL is detected by the chain verification.
            await db.Database.ExecuteSqlAsync($"UPDATE [pos].[TicketLines] SET [UnitPrice] = 1 WHERE [TicketId] = {tickets[2].Id}");
            var stored = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments)
                .Where(t => t.RegisterId == RegisterId).ToListAsync();
            TicketChainVerifier.Verify(RegisterId, stored).Issues.Should().ContainSingle(i => i.Kind == ChainIssueKind.HashMismatch && i.Sequence == 3);
        }

        await using (var db = _database.CreateContext())
        {
            var ticket = await db.Tickets.FirstAsync();
            db.Entry(ticket).Property(t => t.TotalAmount).CurrentValue = 0m;
            await FluentActions.Invoking(() => db.SaveChangesAsync()).Should().ThrowAsync<ImmutableRecordException>();
        }

        await using (var db = _database.CreateContext())
        {
            db.AccountMovements.Remove(await db.AccountMovements.FirstAsync());
            await FluentActions.Invoking(() => db.SaveChangesAsync()).Should().ThrowAsync<ImmutableRecordException>();
        }
    }

    [Fact]
    public async Task Database_rejects_duplicate_ticket_sequence_and_second_open_session()
    {
        var session = new CashSession(Guid.NewGuid(), RegisterId, OperatorId, 0m, DateTimeOffset.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow));
        var tickets = await SeedTicketsAsync(session, 1);

        await using (var db = _database.CreateContext())
        {
            var duplicate = Ticket.Issue(SaleRequest(session, 1, tickets[0].Hash, await LinesAsync(db)));
            db.Tickets.Add(duplicate);
            await FluentActions.Invoking(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        }

        await using (var db = _database.CreateContext())
        {
            db.CashSessions.Add(new CashSession(Guid.NewGuid(), RegisterId, OperatorId, 0m, DateTimeOffset.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow)));
            await FluentActions.Invoking(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Z_report_is_persisted_and_closes_the_session()
    {
        var session = new CashSession(Guid.NewGuid(), RegisterId, OperatorId, 200m, DateTimeOffset.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow));
        var tickets = await SeedTicketsAsync(session, 3);

        await using (var db = _database.CreateContext())
        {
            var tracked = await db.CashSessions.SingleAsync(s => s.Id == session.Id);
            var z = ZReportCalculator.Compute(new ZReportRequest(Guid.NewGuid(), tracked, 1, DateTimeOffset.UtcNow, 200m + 3 * 45m, tickets, []));
            db.ZReports.Add(z);
            tracked.Close(z, OperatorId);
            await db.SaveChangesAsync();
        }

        await using (var db = _database.CreateContext())
        {
            var z = await db.ZReports.Include(r => r.Lines).SingleAsync(r => r.CashSessionId == session.Id);
            z.NetSales.Should().Be(135m);
            z.CashDifference.Should().Be(0m);
            z.Lines.Should().Contain(l => l.Section == ZSection.Vat);
            (await db.CashSessions.SingleAsync(s => s.Id == session.Id)).Status.Should().Be(CashSessionStatus.Closed);
        }
    }

    private async Task<List<Ticket>> SeedTicketsAsync(CashSession session, int count)
    {
        await using var db = _database.CreateContext();
        db.CashSessions.Add(session);
        var lines = await LinesAsync(db);
        var tickets = new List<Ticket>();
        var previous = TicketHasher.GenesisHash;
        for (var i = 1; i <= count; i++)
        {
            var ticket = Ticket.Issue(SaleRequest(session, i, previous, lines));
            tickets.Add(ticket);
            previous = ticket.Hash;
        }

        db.Tickets.AddRange(tickets);
        await db.SaveChangesAsync();
        return tickets;
    }

    private static async Task<TicketLineInput[]> LinesAsync(Persistence.PosDbContext db)
    {
        var couscous = await db.Articles.SingleAsync(a => a.Code == "CSC-PLT");
        var water = await db.Articles.SingleAsync(a => a.Code == "EAU-50");
        return
        [
            new(couscous.Id, couscous.Code, couscous.Name, 1, 38.00m, couscous.VatRate, couscous.IsSubsidizable, LineSource.VisionAuto),
            new(water.Id, water.Code, water.Name, 1, 7.00m, water.VatRate, water.IsSubsidizable),
        ];
    }

    private static TicketIssueRequest SaleRequest(CashSession session, long sequence, string previousHash, TicketLineInput[] lines) => new()
    {
        Id = Guid.NewGuid(),
        RegisterId = RegisterId,
        RegisterPrefix = "CAS1",
        Sequence = sequence,
        PreviousHash = previousHash,
        CashSessionId = session.Id,
        OperatorId = OperatorId,
        BusinessDate = session.BusinessDate,
        IssuedAt = DateTimeOffset.UtcNow,
        Lines = lines,
        Payments = [new PaymentInput(PaymentMethod.Cash, 45m) { Tendered = 50m }],
    };
}
