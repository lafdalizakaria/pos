using System.Net;
using System.Net.Http.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class ScopeTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Site_manager_only_sees_its_site_and_company()
    {
        var manager = ManagerClient();

        (await ReadAsync<List<CompanyDto>>(await manager.GetAsync("/api/v1/companies"))).Select(c => c.Id).Should().Equal(Nfms);
        (await ReadAsync<List<SiteDto>>(await manager.GetAsync("/api/v1/sites"))).Select(s => s.Id).Should().Equal(CasaSite);
        (await ReadAsync<List<PointOfSaleDto>>(await manager.GetAsync("/api/v1/points-of-sale"))).Should().HaveCount(2);
        (await ReadAsync<List<ClientCompanyDto>>(await manager.GetAsync("/api/v1/clients"))).Select(c => c.Id).Should().Equal(Atlas);
        (await manager.GetAsync($"/api/v1/clients/{Sahara}/diners")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.GetAsync($"/api/v1/points-of-sale/{TngSnack}/prices")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_sees_everything()
    {
        (await ReadAsync<List<SiteDto>>(await AdminClient().GetAsync("/api/v1/sites"))).Should().HaveCount(3);
        (await ReadAsync<List<ClientCompanyDto>>(await AdminClient().GetAsync("/api/v1/clients"))).Should().HaveCount(2);
    }

    [Fact]
    public async Task Roles_restrict_writes()
    {
        var category = DemoIds.Category("PLAT");
        var article = new ArticleUpsert("TEST-01", "Article test", null, category, 10m, 0.10m, null);

        var manager = await ManagerClient().PostAsJsonAsync("/api/v1/articles", article);
        manager.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(manager)).Should().Be("forbidden");
        (await ViewerClient().PostAsJsonAsync("/api/v1/clients", new ClientCompanyUpsert(Nfms, "X", "X", null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ManagerClient().PostAsJsonAsync("/api/v1/sites", new SiteUpsert(Nfms, "RAB", "Rabat", "Rabat", null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "creating a site needs a company-wide scope");
        (await ManagerClient().PostAsJsonAsync("/api/v1/companies", new CompanyUpsert("NMI", "NMI", "NMI SA", null, null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ManagerClient().GetAsync("/api/v1/access-scopes")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ManagerClient().GetAsync("/api/v1/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Organisation_lifecycle_with_conflicts_and_audit()
    {
        var admin = AdminClient();
        var company = await ReadAsync<CompanyDto>(await admin.PostAsJsonAsync("/api/v1/companies",
            new CompanyUpsert("nmi", "NMI", "Newrest Maroc Industrie", "000000000000003", null, null, "Tanger")));
        company.Code.Should().Be("NMI");
        (await admin.PostAsJsonAsync("/api/v1/companies", new CompanyUpsert("NMI", "x", "x", null, null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        var site = await ReadAsync<SiteDto>(await admin.PostAsJsonAsync("/api/v1/sites",
            new SiteUpsert(company.Id, "TNG-GZ", "Tanger Gzenaya", "Tanger", null, "Africa/Casablanca")));
        (await admin.PostAsJsonAsync("/api/v1/sites", new SiteUpsert(company.Id, "BAD", "x", "x", null, "Mars/Olympus")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var pos = await ReadAsync<PointOfSaleDto>(await admin.PostAsJsonAsync("/api/v1/points-of-sale",
            new PointOfSaleUpsert(site.Id, "KIOSK", "Kiosque", "Kiosk")));
        (await admin.PostAsJsonAsync("/api/v1/points-of-sale", new PointOfSaleUpsert(site.Id, "X", "x", "Restaurant")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var register = await ReadAsync<RegisterDto>(await admin.PostAsJsonAsync("/api/v1/registers",
            new RegisterUpsert(pos.Id, "C01", "Kiosque caisse 1", "TGZ1")));
        (await admin.PostAsJsonAsync("/api/v1/registers", new RegisterUpsert(pos.Id, "C02", "x", "TGZ1")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/registers/{register.Id}", new RegisterUpsert(pos.Id, "C01", "Renommée", "TGZ9")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "the ticket prefix is immutable");
        (await ReadAsync<RegisterDto>(await admin.PutAsJsonAsync($"/api/v1/registers/{register.Id}",
            new RegisterUpsert(pos.Id, "C01", "Renommée", "TGZ1")))).Name.Should().Be("Renommée");
        (await ReadAsync<SiteDto>(await admin.PutAsJsonAsync($"/api/v1/sites/{site.Id}",
            new SiteUpsert(company.Id, "TNG-GZ", "Gzenaya", "Tanger", "Zone franche", null, false)))).IsActive.Should().BeFalse();
        (await ReadAsync<PointOfSaleDto>(await admin.PutAsJsonAsync($"/api/v1/points-of-sale/{pos.Id}",
            new PointOfSaleUpsert(site.Id, "KIOSK", "Kiosque 2", "Snack")))).Type.Should().Be("Snack");
        (await ReadAsync<CompanyDto>(await admin.PutAsJsonAsync($"/api/v1/companies/{company.Id}",
            new CompanyUpsert("NMI", "NMI", "Newrest Maroc Industrie SA", null, null, null, null)))).LegalName.Should().EndWith("SA");

        var audit = await ReadAsync<PagedResult<AuditEntryDto>>(await admin.GetAsync($"/api/v1/audit?entityType=Register&entityId={register.Id}"));
        audit.Items.Select(a => a.Action).Should().BeEquivalentTo(["Created", "Updated"]);
        audit.Items.Should().OnlyContain(a => a.Actor == Admin);
    }

    [Fact]
    public async Task Operators_management_with_pin_and_lockout_reset()
    {
        var manager = ManagerClient();
        var op = await ReadAsync<OperatorDto>(await manager.PostAsJsonAsync("/api/v1/operators",
            new OperatorCreate(Nfms, CasaSite, "caiss09", "Leila", "Amrani", ["Cashier"], "4321")));
        op.Roles.Should().Equal("Cashier");
        (await manager.PostAsJsonAsync("/api/v1/operators", new OperatorCreate(Nfms, CasaSite, "X1", "a", "b", ["Cashier"], "12")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await manager.PostAsJsonAsync("/api/v1/operators", new OperatorCreate(Nfms, TangerSite, "X2", "a", "b", ["Cashier"], "1234")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PostAsJsonAsync("/api/v1/operators", new OperatorCreate(Nfms, CasaSite, "X3", "a", "b", ["Chef"], "1234")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var updated = await ReadAsync<OperatorDto>(await manager.PutAsJsonAsync($"/api/v1/operators/{op.Id}",
            new OperatorUpdate(CasaSite, "Leila", "Amrani", ["Cashier", "Supervisor"], true)));
        updated.Roles.Should().BeEquivalentTo(["Cashier", "Supervisor"]);
        (await manager.PutAsJsonAsync($"/api/v1/operators/{op.Id}/pin", new SetPinRequest("8642"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await manager.PostAsync($"/api/v1/operators/{op.Id}/unlock", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAsync<List<OperatorDto>>(await manager.GetAsync("/api/v1/operators"))).Should().Contain(o => o.Id == op.Id);

        var audit = await ReadAsync<PagedResult<AuditEntryDto>>(await AdminClient().GetAsync($"/api/v1/audit?entityType=Operator&entityId={op.Id}"));
        audit.Items.Select(a => a.Action).Should().Contain(["Created", "Updated", "PinChanged", "OperatorUnlocked"]);
        audit.Items.Should().OnlyContain(a => a.AfterJson == null || !a.AfterJson.Contains("8642"), "PINs are never audited");
    }

    [Fact]
    public async Task Scopes_are_managed_by_admins()
    {
        var admin = AdminClient();
        var scope = await ReadAsync<AccessScopeDto>(await admin.PostAsJsonAsync("/api/v1/access-scopes",
            new AccessScopeCreate("New.User@Newrest.MA", Nms, null)));
        scope.UserName.Should().Be("new.user@newrest.ma");
        (await admin.PostAsJsonAsync("/api/v1/access-scopes", new AccessScopeCreate("new.user@newrest.ma", Nms, null)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/access-scopes", new AccessScopeCreate("x@newrest.ma", Nms, CasaSite)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "the site belongs to another company");

        (await ReadAsync<List<ClientCompanyDto>>(await AsUser("new.user@newrest.ma", PosRoles.Viewer).GetAsync("/api/v1/clients")))
            .Select(c => c.Id).Should().Equal(Sahara);
        (await admin.DeleteAsync($"/api/v1/access-scopes/{scope.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAsync<List<ClientCompanyDto>>(await AsUser("new.user@newrest.ma", PosRoles.Viewer).GetAsync("/api/v1/clients")))
            .Should().BeEmpty();
    }
}

internal static class DemoIds
{
    public static Guid Category(string code) => Infrastructure.Seeding.DemoDataSeeder.Id($"category:{code}");
}
