using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Api.Operations;
using Newrest.Pos.Application.Operations;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Scenarios.Tests;

[Collection(ScenarioCollection.Name)]
public sealed class SupervisionScenarios(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Cas1 = DemoDataSeeder.Id("register:CAS1");
    private readonly WebhookRecorder _webhook = new();
    private TestDatabase _database = null!;
    private ApiFactory _api = null!;

    private sealed class WebhookRecorder : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync(seedDemo: true);
        _api = new ApiFactory(_database.ConnectionString, b =>
        {
            b.UseSetting("Supervision:WebhookUrl", "https://hooks.example.test/pos");
            b.UseSetting("Supervision:BackOfficeUrl", "https://pos.newrest.ma");
            b.ConfigureTestServices(s => s.AddHttpClient(nameof(SupervisionWorker)).ConfigurePrimaryHttpMessageHandler(() => _webhook));
        });
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<SupervisionDashboardDto> DashboardAsync() =>
        (await _api.Admin().GetFromJsonAsync<SupervisionDashboardDto>("/api/v1/supervision/dashboard"))!;

    private static async Task DrainAsync(RegisterHarness register)
    {
        for (var i = 0; i < 30 && await register.Get<LocalStore>().CountPendingAsync() > 0; i++)
        {
            await register.Get<SyncService>().RunOnceAsync();
        }
    }

    [Fact]
    public async Task Heartbeat_backup_integrity_and_alerts_reach_the_operations_team()
    {
        var ingested = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PosMetrics.MeterName && instrument.Name == "pos.tickets.ingested")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref ingested, value));
        listener.Start();

        await using var register = await RegisterHarness.CreateAsync(_api, Cas1);
        var (cashier, session) = await register.LoginAndOpenAsync();
        for (var i = 0; i < 3; i++)
        {
            await register.Get<SaleService>().CompleteSaleAsync(await register.CartAsync(session, "PAIN"), [new PaymentChoice(PaymentMethod.Cash, 1.5m)],
                cashier, session);
        }

        await register.Get<SyncService>().RunOnceAsync(forceReference: true);
        await DrainAsync(register);
        Interlocked.Read(ref ingested).Should().BeGreaterThanOrEqualTo(3);

        // Local backup taken (consistent copy of the SQLite database) and reported with the heartbeat.
        var backups = Directory.GetFiles(Path.Combine(register.Folder, "backups"), "register-*.db");
        backups.Should().ContainSingle();
        var row = (await DashboardAsync()).Registers.Single(r => r.RegisterId == Cas1);
        row.Should().Match<RegisterSupervisionDto>(r => r.AppVersion != null && r.PendingCount == 0 && r.BlockingError == null
                                                       && r.OpenSessionSince != null && r.LastBackupAt != null && r.IntegrityValid == null);
        (await DashboardAsync()).Alerts.Should().Contain(a => a.Code == "integrity_not_checked" && a.RegisterId == Cas1);

        var run = await (await _api.Admin().PostAsync("/api/v1/supervision/integrity-checks", null)).Content.ReadFromJsonAsync<IntegrityRunDto>();
        run!.Invalid.Should().Be(0);
        var dashboard = await DashboardAsync();
        dashboard.Alerts.Should().NotContain(a => a.RegisterId == Cas1);
        dashboard.Registers.Single(r => r.RegisterId == Cas1).Should().Match<RegisterSupervisionDto>(r => r.IntegrityValid == true && r.TicketsChecked == 3);

        // Someone alters a ticket directly in SQL Server: the next verification raises a critical alert, sent to the webhook.
        await using (var sql = new SqlConnection(_database.ConnectionString))
        {
            await sql.OpenAsync();
            await using var command = new SqlCommand(
                "UPDATE l SET UnitPrice = 0.5 FROM pos.TicketLines l JOIN pos.Tickets t ON t.Id = l.TicketId WHERE t.Sequence = 2 AND t.RegisterId = @r", sql);
            command.Parameters.AddWithValue("@r", Cas1);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var worker = _api.Services.GetRequiredService<SupervisionWorker>();
        await _api.Services.CreateScope().ServiceProvider.GetRequiredService<SupervisionService>().RunIntegrityChecksSystemAsync();
        await worker.RunOnceAsync(CancellationToken.None);
        var alert = (await DashboardAsync()).Alerts.Single(a => a.Code == "integrity_failed");
        alert.Severity.Should().Be("Critical");
        alert.Message.Should().Contain("n° 2 : contenu modifié");
        _webhook.Bodies.Should().ContainSingle().Which.Should().Contain("CAS1").And.Contain("contenu modifi").And.Contain("https://pos.newrest.ma/supervision");
        await worker.RunOnceAsync(CancellationToken.None);
        _webhook.Bodies.Should().HaveCount(1, "an alert already sent is not repeated before the re-notification delay");
    }

    [Fact]
    public async Task Debit_without_ticket_and_blocked_queue_are_reported()
    {
        // A register debits an account online, then its ticket never arrives (response lost + offline refusal).
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1);
        var (cashier, _) = await register.LoginAndOpenAsync();
        var api = register.Get<PosApiClient>();
        var account = DemoDataSeeder.Id("account:ATL0002");
        await api.PostAccountMovementAsync(new AccountMovementSyncDto(Guid.NewGuid(), account, "Consumption", -12.5m, DateTimeOffset.UtcNow.AddHours(-3),
            "Account", Guid.NewGuid(), cashier.Id, null, false));
        await api.SendHeartbeatAsync(new RegisterHeartbeatDto("1.6.0", 4, DateTimeOffset.UtcNow.AddMinutes(-5), "Ticket refusé (hash_mismatch)", 7,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow));

        var alerts = (await DashboardAsync()).Alerts;
        alerts.Should().Contain(a => a.Code == "queue_blocked" && a.Severity == "Critical" && a.Message.Contains("hash_mismatch"));
        alerts.Should().Contain(a => a.Code == "debit_without_ticket" && a.Message.Contains("12,50 MAD"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var reconciliation = await _api.Admin().GetFromJsonAsync<ReconciliationDto>(
            $"/api/v1/supervision/reconciliation?from={today.AddDays(-1):yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        reconciliation!.DebitsWithoutTicket.Should().ContainSingle().Which.Should()
            .Match<DebitWithoutTicketDto>(d => d.Amount == 12.5m && d.RegisterPrefix == "CAS1" && d.DinerName == "Khadija El Amrani");

        // Finance reverses the debit: the anomaly disappears.
        var movement = reconciliation.DebitsWithoutTicket[0].MovementId;
        var reversal = await _api.Admin().PostAsJsonAsync($"/api/v1/accounts/{account}/movements/{movement}/reverse",
            new ReversalRequest("Ticket jamais reçu (rapprochement)", Guid.NewGuid()));
        reversal.StatusCode.Should().Be(HttpStatusCode.OK, await reversal.Content.ReadAsStringAsync());
        (await _api.Admin().GetFromJsonAsync<ReconciliationDto>(
            $"/api/v1/supervision/reconciliation?from={today.AddDays(-1):yyyy-MM-dd}&to={today:yyyy-MM-dd}"))!.DebitsWithoutTicket.Should().BeEmpty();

        // Supervision is scoped: a Tangier-only user does not see Casablanca registers.
        var tangierViewer = _api.CreateClient();
        (await _api.Admin().PostAsJsonAsync("/api/v1/access-scopes", new AccessScopeCreate("tanger@newrest.ma", DemoDataSeeder.Id("company:NFMS"),
            DemoDataSeeder.Id("site:TNG-TFZ")))).EnsureSuccessStatusCode();
        tangierViewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("tanger@newrest.ma", PosRoles.Viewer));
        var scoped = (await tangierViewer.GetFromJsonAsync<SupervisionDashboardDto>("/api/v1/supervision/dashboard"))!;
        scoped.Registers.Should().OnlyContain(r => r.SiteName == "Tanger Free Zone");
        scoped.Alerts.Should().BeEmpty();
        (await tangierViewer.PostAsync("/api/v1/supervision/integrity-checks", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Supervisor_screen_retries_the_queue_and_forces_a_close()
    {
        await using var register = await RegisterHarness.CreateAsync(_api, Cas1);
        var (cashier, session) = await register.LoginAndOpenAsync();
        await register.Get<SaleService>().CompleteSaleAsync(await register.CartAsync(session, "PAIN"), [new PaymentChoice(PaymentMethod.Cash, 1.5m)],
            cashier, session);
        register.Network.Mode = NetworkMode.Down;
        register.Get<OperatorLoginService>().Logout();
        (await register.Get<OperatorLoginService>().LoginAsync("RESP01", "5678")).Should().Be(LoginOutcome.Success);

        var screen = register.Get<SupervisorViewModel>();
        await screen.LoadAsync();
        screen.IsSupervisor.Should().BeTrue();
        screen.Items.Should().HaveCountGreaterThanOrEqualTo(2).And.OnlyContain(i => i.Status == "En attente");
        await screen.BackupNowCommand.ExecuteAsync(null);
        screen.BackupLine.Should().StartWith("Dernière sauvegarde locale");

        register.Network.Mode = NetworkMode.Up;
        await screen.SyncNowCommand.ExecuteAsync(null);
        screen.Items.Should().BeEmpty();

        var close = register.Get<CloseSessionViewModel>();
        close.CountedCash = 201.5m;
        await close.ForceCloseCommand.ExecuteAsync(null);
        close.Message.Should().Contain("(forcée)");
        await close.LastBackup;
        await DrainAsync(register);
        var z = (await _api.Admin().GetFromJsonAsync<List<ZReportDto>>($"/api/v1/z-reports?registerId={Cas1}"))!.Single();
        z.ZNumber.Should().Be(1);
        Directory.GetFiles(Path.Combine(register.Folder, "backups")).Should().HaveCount(2, "a backup is taken after each Z");
    }
}
