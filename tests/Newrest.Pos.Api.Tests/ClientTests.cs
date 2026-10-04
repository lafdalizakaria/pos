using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClosedXML.Excel;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class ClientTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Client_contract_and_subsidy_rule_lifecycle()
    {
        var admin = AdminClient();
        var client = await ReadAsync<ClientCompanyDto>(await admin.PostAsJsonAsync("/api/v1/clients",
            new ClientCompanyUpsert(Nfms, "rif", "Rif Electronics (fictif)", "000000000000201", "Tanger")));
        (await admin.PostAsJsonAsync("/api/v1/clients", new ClientCompanyUpsert(Nfms, "RIF", "x", null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ClientCompanyDto>(await admin.PutAsJsonAsync($"/api/v1/clients/{client.Id}",
            new ClientCompanyUpsert(Nfms, "RIF", "Rif Electronics", null, "Tanger Med")))).Name.Should().Be("Rif Electronics");

        var contract = await ReadAsync<ContractDto>(await admin.PostAsJsonAsync("/api/v1/contracts",
            new ContractUpsert(client.Id, "CTR-RIF-2027", new DateOnly(2027, 1, 1), null, "PayrollDeduction", [TngSnack, TngSnack])));
        contract.PointOfSaleIds.Should().Equal(TngSnack);
        (await admin.PostAsJsonAsync("/api/v1/contracts",
                new ContractUpsert(client.Id, "CTR-RIF-X", new DateOnly(2027, 1, 1), null, "EmployerInvoice", [KenSelf])))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "Kénitra belongs to NMS");
        contract = await ReadAsync<ContractDto>(await admin.PutAsJsonAsync($"/api/v1/contracts/{contract.Id}",
            new ContractUpsert(client.Id, "CTR-RIF-2027", new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31), "EmployerInvoice", [CasaSelf])));
        contract.PointOfSaleIds.Should().Equal(CasaSelf);
        contract.BillingMode.Should().Be("EmployerInvoice");

        var rule = await ReadAsync<SubsidyRuleDto>(await admin.PostAsJsonAsync($"/api/v1/contracts/{contract.Id}/subsidy-rules",
            new SubsidyRuleCreate("50 % plafonné", "Percentage", 50m, new DateOnly(2027, 1, 1), null, 20m, null, null, null)));
        (await admin.PostAsJsonAsync($"/api/v1/contracts/{contract.Id}/subsidy-rules",
                new SubsidyRuleCreate("x", "Percentage", 150m, new DateOnly(2027, 1, 1), null, null, null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var closed = await ReadAsync<SubsidyRuleDto>(await admin.PostAsJsonAsync($"/api/v1/subsidy-rules/{rule.Id}/close",
            new SubsidyRuleClose(new DateOnly(2027, 6, 30))));
        closed.ValidTo.Should().Be(new DateOnly(2027, 6, 30));
        (await admin.PostAsJsonAsync($"/api/v1/subsidy-rules/{rule.Id}/close", new SubsidyRuleClose(new DateOnly(2027, 9, 30))))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a closed rule cannot be extended");

        var reloaded = await ReadAsync<ContractDto>(await admin.GetAsync($"/api/v1/contracts/{contract.Id}"));
        reloaded.SubsidyRules.Should().ContainSingle();
        (await ReadAsync<List<ContractDto>>(await admin.GetAsync($"/api/v1/clients/{client.Id}/contracts"))).Should().ContainSingle();
        (await ManagerClient().PostAsJsonAsync($"/api/v1/contracts/{contract.Id}/subsidy-rules",
                new SubsidyRuleCreate("x", "FixedAmount", 10m, new DateOnly(2027, 1, 1), null, null, null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "contracts are managed at company level");
    }

    [Fact]
    public async Task Diner_badges_lost_and_blocked()
    {
        var manager = ManagerClient();
        var diner = await ReadAsync<DinerDto>(await manager.PostAsJsonAsync("/api/v1/diners",
            new DinerCreate(Atlas, AtlasContract, "M9001", "Samir", "Haddad", "cadre", "Prepaid", 0m, "bdg-9001")));
        diner.Category.Should().Be("CADRE");
        diner.Badges.Should().ContainSingle().Which.Number.Should().Be("BDG-9001");
        diner.Accounts.Should().ContainSingle().Which.Balance.Should().Be(0m);
        (await manager.PostAsJsonAsync("/api/v1/diners", new DinerCreate(Atlas, AtlasContract, "M9001", "a", "b", null, "Prepaid", 0m, null)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await manager.PostAsJsonAsync("/api/v1/diners", new DinerCreate(Atlas, AtlasContract, "M9002", "a", "b", null, "Prepaid", 0m, "BDG-ATL0001")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "badge numbers are unique");
        (await manager.PostAsJsonAsync("/api/v1/diners", new DinerCreate(Atlas, DemoDataSeeder.Id("contract:SAHARA"), "M9003", "a", "b", null, "Prepaid", 0m, null)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var badgeId = diner.Badges[0].Id;
        (await manager.PostAsJsonAsync($"/api/v1/diners/{diner.Id}/badges", new BadgeIssue("BDG-9999")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "already has an active badge");
        var replacement = await ReadAsync<BadgeDto>(await manager.PostAsJsonAsync($"/api/v1/badges/{badgeId}/lost", new BadgeReplace("BDG-9001B")));
        replacement.Status.Should().Be("Active");
        (await manager.PostAsJsonAsync($"/api/v1/badges/{badgeId}/lost", new BadgeReplace("BDG-9001C")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var byBadge = await ReadAsync<DinerDto>(await manager.GetAsync("/api/v1/diners/by-badge/bdg-9001b"));
        byBadge.Id.Should().Be(diner.Id);
        byBadge.Badges.Select(b => b.Status).Should().BeEquivalentTo(["Active", "Lost"]);
        byBadge.Accounts.Should().ContainSingle("the account (and balance) is untouched");

        (await ReadAsync<BadgeDto>(await manager.PostAsync($"/api/v1/badges/{replacement.Id}/block", null))).Status.Should().Be("Blocked");
        (await ReadAsync<BadgeDto>(await manager.PostAsync($"/api/v1/badges/{replacement.Id}/unblock", null))).Status.Should().Be("Active");

        var updated = await ReadAsync<DinerDto>(await manager.PutAsJsonAsync($"/api/v1/diners/{diner.Id}", new DinerUpdate("Samir", "Haddad-Alami", null, false)));
        updated.IsActive.Should().BeFalse();
        updated.Category.Should().BeNull();
        var search = await ReadAsync<PagedResult<DinerDto>>(await manager.GetAsync($"/api/v1/clients/{Atlas}/diners?search=BDG-9001B"));
        search.Items.Should().ContainSingle().Which.Id.Should().Be(diner.Id);

        var audit = await ReadAsync<PagedResult<AuditEntryDto>>(await AdminClient().GetAsync($"/api/v1/audit?entityType=Badge&entityId={badgeId}"));
        audit.Items.Should().ContainSingle(a => a.Action == "BadgeLost");
    }

    [Fact]
    public async Task Csv_import_creates_updates_and_reports_errors_without_partial_changes()
    {
        var csv = """"
            Matricule;Nom;Prénom;Catégorie;Badge;TypeCompte;Découvert;Actif
            MATL0001;Benali;Youssef;CADRE;;Prepaid;;oui
            MATL0002;El Amrani;Khadija;;;Mixed;150;
            MATL0003;Chraibi-Modifié;Mehdi;;BDG-OTHER;;;
            N100;"Ait Lahcen; dit ""Lahcen""";Hassan;;BDG-N100;Postpaid;500;
            N101;Kabbaj;Sara;;BDG-ATL0005;;;
            N102;Ziani;Anas;;;Prepaid;abc;
            N103;;Imad;;;;;
            N100;Doublon;X;;;;;
            """";

        var dry = await ImportAsync(Encoding.UTF8.GetBytes(csv), "convives.csv", dryRun: true);
        dry.Created.Should().Be(1);
        (await ReadAsync<PagedResult<DinerDto>>(await AdminClient().GetAsync($"/api/v1/clients/{Atlas}/diners?search=N100"))).TotalCount.Should().Be(0);

        var result = await ImportAsync(Encoding.UTF8.GetBytes(csv), "convives.csv");

        result.Created.Should().Be(1);
        result.Updated.Should().Be(1);
        result.Unchanged.Should().Be(1);
        result.Errors.Select(e => (e.Line, e.EmployeeNumber)).Should().BeEquivalentTo(new[]
        {
            (4, "MATL0003"), (6, "N101"), (7, "N102"), (8, "N103"), (9, "N100"),
        });
        result.Errors.Single(e => e.Line == 4).Message.Should().Contain("remplacement de badge");

        var mehdi = (await ReadAsync<PagedResult<DinerDto>>(await AdminClient().GetAsync($"/api/v1/clients/{Atlas}/diners?search=ATL0003"))).Items.Single();
        mehdi.LastName.Should().Be("Chraibi", "a rejected line leaves no partial change");
        var hassan = (await ReadAsync<PagedResult<DinerDto>>(await AdminClient().GetAsync($"/api/v1/clients/{Atlas}/diners?search=N100"))).Items.Single();
        hassan.LastName.Should().Be("Ait Lahcen; dit \"Lahcen\"");
        hassan.Accounts.Single().Should().Match<AccountSummaryDto>(a => a.Type == "Postpaid" && a.OverdraftLimit == 500m);
        var khadija = (await ReadAsync<PagedResult<DinerDto>>(await AdminClient().GetAsync($"/api/v1/clients/{Atlas}/diners?search=ATL0002"))).Items.Single();
        khadija.Accounts.Single().Should().Match<AccountSummaryDto>(a => a.Type == "Mixed" && a.OverdraftLimit == 150m && a.Balance == 150m);
    }

    [Fact]
    public async Task Excel_import_and_invalid_files()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Convives");
        sheet.Cell(1, 1).Value = "EmployeeNumber";
        sheet.Cell(1, 2).Value = "LastName";
        sheet.Cell(1, 3).Value = "FirstName";
        sheet.Cell(1, 4).Value = "Badge";
        for (var i = 0; i < 30; i++)
        {
            sheet.Cell(i + 2, 1).Value = $"X{i:D3}";
            sheet.Cell(i + 2, 2).Value = $"Nom{i}";
            sheet.Cell(i + 2, 3).Value = $"Prénom{i}";
            sheet.Cell(i + 2, 4).Value = $"BX{i:D3}";
        }

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        var result = await ImportAsync(buffer.ToArray(), "convives.xlsx");
        result.Created.Should().Be(30);
        result.Errors.Should().BeEmpty();

        var badFormat = await PostFileAsync(Encoding.UTF8.GetBytes("hello"), "convives.pdf");
        badFormat.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(badFormat)).Should().Be("invalid_file");
        (await PostFileAsync(Encoding.UTF8.GetBytes("Nom;Prénom\nA;B"), "convives.csv")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await PostFileAsync([1, 2, 3], "convives.xlsx")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await PostFileAsync(Encoding.UTF8.GetBytes("Matricule,Nom,Prénom\nX1,\"open"), "c.csv")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    private async Task<DinerImportResult> ImportAsync(byte[] content, string fileName, bool dryRun = false) =>
        await ReadAsync<DinerImportResult>(await PostFileAsync(content, fileName, dryRun));

    private Task<HttpResponseMessage> PostFileAsync(byte[] content, string fileName, bool dryRun = false)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        return ManagerClient().PostAsync($"/api/v1/clients/{Atlas}/diners/import?contractId={AtlasContract}&dryRun={dryRun}", form);
    }
}
