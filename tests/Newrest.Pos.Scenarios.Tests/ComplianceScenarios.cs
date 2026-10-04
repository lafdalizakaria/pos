using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Newrest.Pos.Application.Compliance;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Scenarios.Tests;

[Collection(ScenarioCollection.Name)]
public sealed class ComplianceScenarios(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Nfms = DemoDataSeeder.Id("company:NFMS");
    private TestDatabase _database = null!;
    private ApiFactory _api = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync(seedDemo: true);
        _api = new ApiFactory(_database.ConnectionString);
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    /// <summary>A register living 70 days ago: its months are closed today.</summary>
    private Task<RegisterHarness> PastRegisterAsync() => RegisterHarness.CreateAsync(_api, DemoDataSeeder.Id("register:CAS1"),
        configure: s => s.AddSingleton<TimeProvider>(new ShiftedClock(TimeSpan.FromDays(-70))));

    private static async Task SellBreadAsync(RegisterHarness register, LoggedOperator cashier, Client.Data.LocalCashSession session, int count)
    {
        var bread = (await register.Get<ReferenceCache>().GetArticlesAsync()).Values.Single(a => a.Code == "PAIN");
        for (var i = 0; i < count; i++)
        {
            var cart = new Cart();
            cart.Add(new DailyMenuItemDto(bread.Id, bread.Code, bread.Name, bread.CategoryId, 1.5m, true, 1), bread.VatRate, bread.IsSubsidizable);
            await register.Get<SaleService>().CompleteSaleAsync(cart, [new PaymentChoice(PaymentMethod.Cash, 1.5m)], cashier, session);
        }
    }

    private static async Task DrainAsync(RegisterHarness register)
    {
        for (var i = 0; i < 40 && await register.Get<Client.Core.Local.LocalStore>().CountPendingAsync() > 0; i++)
        {
            await register.Get<SyncService>().RunOnceAsync();
        }
    }

    private async Task<byte[]> DownloadAsync(Guid id) => await _api.Admin().GetByteArrayAsync($"/api/v1/compliance/archives/{id}/file");

    [Fact]
    public async Task Months_are_sealed_in_signed_archives_that_verify_offline_and_continue_each_chain()
    {
        await using var register = await PastRegisterAsync();
        var (supervisor, session) = await register.LoginAndOpenAsync("RESP01", "5678");
        await register.Get<SyncService>().RunOnceAsync(forceReference: true);
        await SellBreadAsync(register, supervisor, session, 3);
        var first = (await register.Get<AccountOperationsService>().ListSessionTicketsAsync(session.Id)).OrderBy(t => t.Sequence).First();
        await register.Get<AccountOperationsService>().IssueCreditNoteAsync(first.Id, "Erreur de saisie", supervisor, session);
        await register.Get<CashSessionService>().CloseAsync(supervisor.Id, 203m);
        await DrainAsync(register);

        var month = DateTime.UtcNow.AddDays(-70);
        var admin = _api.Admin();
        var created = await admin.PostAsJsonAsync("/api/v1/compliance/archives", new ArchiveCreate(Nfms, month.Year, month.Month));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var archive = (await created.Content.ReadFromJsonAsync<ArchiveDto>())!;
        archive.TicketCount.Should().Be(4);
        (await admin.PostAsJsonAsync("/api/v1/compliance/archives", new ArchiveCreate(Nfms, month.Year, month.Month))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "a month is sealed once");
        (await admin.PostAsJsonAsync("/api/v1/compliance/archives", new ArchiveCreate(Nfms, DateTime.UtcNow.Year, DateTime.UtcNow.Month))).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity, "the current month is not closed");

        var bytes = await DownloadAsync(archive.Id);
        ArchiveFormat.Sha256(bytes).Should().Be(archive.Sha256);
        var check = ArchiveFormat.Verify(new MemoryStream(bytes), archive.KeyId);
        check.Issues.Should().BeEmpty();
        check.Should().Match<ArchiveVerificationDto>(c => c.IsValid && c.Tickets == 4 && c.ZReports == 1);
        (await (await admin.PostAsync($"/api/v1/compliance/archives/{archive.Id}/verify", null)).Content.ReadFromJsonAsync<ArchiveVerificationDto>())!
            .IsValid.Should().BeTrue();

        // Offline verification with the Migrator tool (what an auditor would run).
        var file = Path.Combine(register.Folder, "archive.zip");
        await File.WriteAllBytesAsync(file, bytes);
        var migrator = Path.Combine(AppContext.BaseDirectory.Replace("Newrest.Pos.Scenarios.Tests", "Newrest.Pos.Migrator").Replace("/tests/", "/src/"),
            "Newrest.Pos.Migrator.dll");
        if (File.Exists(migrator))
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", [migrator, "--verify-archive", file]) { RedirectStandardOutput = true })!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, output);
            output.Should().Contain("RÉSULTAT : archive intègre").And.Contain("4 ticket(s)");
        }

        // Tampering: a ticket line changed inside the archive, then also its declared hash: both are detected.
        Tamper(bytes, "tickets.jsonl", s => s.Replace("\"unitPrice\":1.5", "\"unitPrice\":0.5"), updateManifest: false).Issues.Should()
            .Contain(i => i.Contains("tickets.jsonl") && i.Contains("modifié"));
        var priced = Tamper(bytes, "tickets.jsonl", s => s.Replace("\"unitPrice\":1.5", "\"unitPrice\":0.5"), updateManifest: true);
        priced.Issues.Should().Contain("Signature du manifeste invalide.").And.Contain(i => i.Contains("ticket invalide (payment_mismatch)"));
        var relabelled = Tamper(bytes, "tickets.jsonl", s => s.Replace("\"label\":\"Pain\"", "\"label\":\"Pain complet\""), updateManifest: true);
        relabelled.Issues.Should().Contain("Signature du manifeste invalide.").And.Contain(i => i.Contains("contenu modifié"));

        // A ticket of the sealed month arrives late (register was offline): it goes into the next archive, chain continued.
        var (cashier2, session2) = await register.LoginAndOpenAsync();
        await SellBreadAsync(register, cashier2, session2, 1);
        await DrainAsync(register);
        var next = month.AddMonths(1);
        var second = (await (await admin.PostAsJsonAsync("/api/v1/compliance/archives", new ArchiveCreate(Nfms, next.Year, next.Month)))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ArchiveDto>())!;
        second.TicketCount.Should().Be(1);
        var secondBytes = await DownloadAsync(second.Id);
        using var previousZip = new ZipArchive(new MemoryStream(bytes));
        var previous = System.Text.Json.JsonSerializer.Deserialize<ArchiveManifest>(previousZip.GetEntry("manifest.json")!.Open(), ArchiveFormat.Json)!;
        var continued = ArchiveFormat.Verify(new MemoryStream(secondBytes), archive.KeyId, previous.Registers);
        continued.Issues.Should().BeEmpty();
        previous.Registers.Single(r => r.Prefix == "CAS1").LastSequence.Should().Be(4);
        (await (await admin.PostAsync($"/api/v1/compliance/archives/{second.Id}/verify", null)).Content.ReadFromJsonAsync<ArchiveVerificationDto>())!
            .IsValid.Should().BeTrue();

        // Archives are listed for finance only.
        (await admin.GetFromJsonAsync<List<ArchiveDto>>("/api/v1/compliance/archives"))!.Should().HaveCount(2);
        var viewer = _api.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token("viewer@newrest.ma", PosRoles.Viewer));
        (await viewer.GetAsync("/api/v1/compliance/archives")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Diners_who_left_are_anonymised_after_a_dry_run()
    {
        var diner = DemoDataSeeder.Id("diner:SAH0003");
        await using (var sql = new SqlConnection(_database.ConnectionString))
        {
            await sql.OpenAsync();
            // The diner left 14 months ago; their account was closed at zero.
            await using var command = new SqlCommand("""
                UPDATE pos.Diners SET IsActive = 0, UpdatedAt = DATEADD(month, -14, SYSUTCDATETIME()) WHERE Id = @d;
                UPDATE pos.Accounts SET IsActive = 0, CachedBalance = 0 WHERE DinerId = @d;
                UPDATE m SET OccurredAt = DATEADD(month, -14, OccurredAt) FROM pos.AccountMovements m JOIN pos.Accounts a ON a.Id = m.AccountId WHERE a.DinerId = @d;
                """, sql);
            command.Parameters.AddWithValue("@d", diner);
            await command.ExecuteNonQueryAsync();
        }

        var admin = _api.Admin();
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-12));
        var simulated = await (await admin.PostAsJsonAsync("/api/v1/compliance/anonymization", new AnonymizationRequest(cutoff, true)))
            .Content.ReadFromJsonAsync<AnonymizationResult>();
        simulated.Should().Be(new AnonymizationResult(1, 1, true));
        (await admin.GetFromJsonAsync<DinerDto>($"/api/v1/diners/{diner}"))!.LastName.Should().NotStartWith("ANON");

        (await (await admin.PostAsJsonAsync("/api/v1/compliance/anonymization", new AnonymizationRequest(cutoff, false)))
            .Content.ReadFromJsonAsync<AnonymizationResult>()).Should().Be(new AnonymizationResult(1, 1, false));
        var anonymous = (await admin.GetFromJsonAsync<DinerDto>($"/api/v1/diners/{diner}"))!;
        anonymous.FirstName.Should().Be("Anonymisé");
        anonymous.EmployeeNumber.Should().StartWith("ANON-");
        (await (await admin.PostAsJsonAsync("/api/v1/compliance/anonymization", new AnonymizationRequest(cutoff, false)))
            .Content.ReadFromJsonAsync<AnonymizationResult>())!.Diners.Should().Be(0, "already anonymised");
        (await admin.PostAsJsonAsync("/api/v1/compliance/anonymization", new AnonymizationRequest(DateOnly.FromDateTime(DateTime.UtcNow), true)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a cut-off must be at least 3 months old");
    }

    private static ArchiveVerificationDto Tamper(byte[] original, string name, Func<string, string> change, bool updateManifest)
    {
        using var buffer = new MemoryStream();
        buffer.Write(original);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            string Read(string entry)
            {
                using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
                return reader.ReadToEnd();
            }

            void Write(string entry, string content)
            {
                zip.GetEntry(entry)!.Delete();
                using var stream = zip.CreateEntry(entry).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }

            var oldContent = Read(name);
            var newContent = change(oldContent);
            newContent.Should().NotBe(oldContent);
            Write(name, newContent);
            if (updateManifest)
            {
                Write("manifest.json", Read("manifest.json").Replace(ArchiveFormat.Sha256(Encoding.UTF8.GetBytes(oldContent)),
                    ArchiveFormat.Sha256(Encoding.UTF8.GetBytes(newContent))));
            }
        }

        buffer.Position = 0;
        return ArchiveFormat.Verify(buffer);
    }
}
