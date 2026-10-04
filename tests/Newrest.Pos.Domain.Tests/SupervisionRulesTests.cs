using Newrest.Pos.Domain.Operations;

namespace Newrest.Pos.Domain.Tests;

public class SupervisionRulesTests
{
    private static readonly DateTimeOffset Now = TestData.Now;
    private static readonly SupervisionThresholds Thresholds = new();

    private static RegisterHeartbeat Heartbeat(Guid id, int pending = 0, DateTimeOffset? oldest = null, string? blocked = null, DateTimeOffset? backup = null,
        DateTimeOffset? at = null)
    {
        var hb = new RegisterHeartbeat(id);
        hb.Record("1.6.0", pending, oldest, blocked, 10, null, backup ?? Now.AddHours(-1), at ?? Now.AddMinutes(-1));
        return hb;
    }

    private static RegisterSnapshot Register(RegisterHeartbeat? hb = null, DateTimeOffset? lastSeen = null, DateTimeOffset? open = null,
        IntegrityCheck? integrity = null, bool hasTickets = false, bool? visionOk = null, string? visionError = null)
    {
        var id = hb?.RegisterId ?? Guid.NewGuid();
        return new RegisterSnapshot(id, "CAS1", "Casablanca", lastSeen, hb, open, integrity, hasTickets, visionOk, visionError,
            visionOk is null ? null : Now.AddMinutes(-5));
    }

    private static IReadOnlyList<Alert> Evaluate(params RegisterSnapshot[] registers) =>
        SupervisionRules.Evaluate(new SupervisionSnapshot(registers, [], 0), Thresholds, Now);

    [Fact]
    public void A_healthy_register_raises_nothing()
    {
        var id = Guid.NewGuid();
        Evaluate(Register(Heartbeat(id), Now.AddMinutes(-2), Now.AddHours(-3), new IntegrityCheck(id, Now.AddHours(-5), 40, true, ""), true, true))
            .Should().BeEmpty();
        Evaluate(Register(lastSeen: Now.AddDays(-3))).Should().BeEmpty("a closed register may be switched off");
    }

    [Fact]
    public void Open_register_without_news_is_critical()
    {
        var alerts = Evaluate(Register(lastSeen: Now.AddMinutes(-45), open: Now.AddHours(-2)));
        alerts.Should().ContainSingle().Which.Should().Match<Alert>(a => a.Code == "register_silent" && a.Severity == AlertSeverity.Critical
                                                                        && a.Message.Contains("45 min"));
        Evaluate(Register(open: Now.AddHours(-3))).Single().Message.Should().Contain("3 h");
    }

    [Fact]
    public void Sync_problems_session_and_backup()
    {
        var blocked = Evaluate(Register(Heartbeat(Guid.NewGuid(), pending: 3, blocked: "Ticket refusé (hash_mismatch)"), Now));
        blocked.Single().Code.Should().Be("queue_blocked");
        Evaluate(Register(Heartbeat(Guid.NewGuid(), pending: 60), Now)).Single().Code.Should().Be("sync_backlog");
        Evaluate(Register(Heartbeat(Guid.NewGuid(), pending: 2, oldest: Now.AddHours(-2)), Now)).Single().Message.Should().Contain("2 h");

        var codes = Evaluate(Register(Heartbeat(Guid.NewGuid(), backup: Now.AddDays(-3)), Now, Now.AddHours(-26))).Select(a => a.Code);
        codes.Should().BeEquivalentTo(["session_not_closed", "backup_missing"]);
    }

    [Fact]
    public void Integrity_and_vision()
    {
        var id = Guid.NewGuid();
        var failed = Evaluate(Register(lastSeen: Now, integrity: new IntegrityCheck(id, Now.AddHours(-1), 12, false, "n° 3 : contenu modifié"), hasTickets: true));
        failed.Single().Should().Match<Alert>(a => a.Code == "integrity_failed" && a.Severity == AlertSeverity.Critical && a.Message.EndsWith("modifié"));
        Evaluate(Register(lastSeen: Now, integrity: new IntegrityCheck(id, Now.AddDays(-3), 12, true, ""), hasTickets: true)).Single().Code
            .Should().Be("integrity_not_checked");
        Evaluate(Register(lastSeen: Now, visionOk: false, visionError: "Modèle altéré")).Single().Code.Should().Be("vision_error");
    }

    [Fact]
    public void Ledger_reconciliation_alerts_and_ordering()
    {
        var snapshot = new SupervisionSnapshot([Register(lastSeen: Now, open: Now.AddHours(-1)), Register(Heartbeat(Guid.NewGuid(), blocked: "x"), Now)],
            [(Guid.NewGuid(), "CAS1", 12.5m, Now.AddHours(-3)), (Guid.NewGuid(), "CAS1", 5m, Now.AddMinutes(-10))], 2);
        var alerts = SupervisionRules.Evaluate(snapshot, Thresholds, Now);
        alerts.Select(a => a.Code).Should().Equal("queue_blocked", "debit_without_ticket", "offline_overdraft");
        alerts.Single(a => a.Code == "debit_without_ticket").Message.Should().StartWith("1 débit(s)").And.Contain("12,50 MAD");
    }

    [Fact]
    public void Heartbeat_values_are_bounded()
    {
        var hb = new RegisterHeartbeat(Guid.NewGuid());
        hb.Record("1.0", -5, null, new string('e', 400), -1, null, null, Now);
        (hb.PendingCount, hb.LocalLastSequence, hb.BlockingError!.Length).Should().Be((0, 0, 300));
        new IntegrityCheck(Guid.NewGuid(), Now, 1, false, new string('x', 3000)).IssuesSummary.Should().HaveLength(2000);
    }
}
