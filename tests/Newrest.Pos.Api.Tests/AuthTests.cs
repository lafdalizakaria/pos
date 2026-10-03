using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class AuthTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    private static readonly Guid Cas1 = DemoDataSeeder.Id("register:CAS1");

    [Fact]
    public async Task Anonymous_and_invalid_tokens_are_rejected()
    {
        (await Anonymous().GetAsync("/api/v1/companies")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Anonymous().GetAsync("/api/v1/ping")).StatusCode.Should().Be(HttpStatusCode.OK);

        var forged = Anonymous();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            UserToken(Admin, [PosRoles.Admin], key: "another-key-that-is-long-enough-0123456789"));
        (await forged.GetAsync("/api/v1/companies")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var expired = Anonymous();
        expired.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            UserToken(Admin, [PosRoles.Admin], expires: DateTime.UtcNow.AddMinutes(-5)));
        (await expired.GetAsync("/api/v1/companies")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_user_without_back_office_role_is_forbidden()
    {
        (await AsUser("someone@newrest.ma").GetAsync("/api/v1/companies")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Me_returns_roles_and_scopes()
    {
        var me = await ReadAsync<CurrentUserDto>(await ManagerClient().GetAsync("/api/v1/auth/me"));

        me.Name.Should().Be(CasaManager);
        me.IsGlobal.Should().BeFalse();
        me.Roles.Should().Equal(PosRoles.Manager);
        me.Scopes.Should().ContainSingle().Which.SiteId.Should().Be(CasaSite);
        (await ReadAsync<CurrentUserDto>(await AdminClient().GetAsync("/api/v1/auth/me"))).IsGlobal.Should().BeTrue();
    }

    [Fact]
    public async Task Register_exchanges_its_device_key_for_a_short_lived_token()
    {
        var issued = await ReadAsync<DeviceKeyIssued>(await ManagerClient().PostAsync($"/api/v1/registers/{Cas1}/device-key", null));
        issued.DeviceKey.Should().StartWith("nrpos_");

        var token = await ReadAsync<RegisterTokenResponse>(await Anonymous().PostAsJsonAsync("/api/v1/auth/register-token",
            new RegisterTokenRequest(Cas1, issued.DeviceKey)));
        token.TicketPrefix.Should().Be("CAS1");
        token.PointOfSaleId.Should().Be(CasaSelf);
        token.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));

        var register = Anonymous();
        register.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        (await register.GetAsync("/api/v1/auth/register/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await register.GetAsync("/api/v1/companies")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a register is not a back-office user");
        (await AdminClient().GetAsync("/api/v1/auth/register/me")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var registers = await ReadAsync<List<RegisterDto>>(await AdminClient().GetAsync($"/api/v1/registers?pointOfSaleId={CasaSelf}"));
        registers.Single(r => r.Id == Cas1).Should().Match<RegisterDto>(r => r.HasDeviceKey && r.LastSeenAt != null);
    }

    [Fact]
    public async Task Wrong_rotated_or_revoked_keys_are_rejected()
    {
        var first = await ReadAsync<DeviceKeyIssued>(await AdminClient().PostAsync($"/api/v1/registers/{Cas1}/device-key", null));
        var second = await ReadAsync<DeviceKeyIssued>(await AdminClient().PostAsync($"/api/v1/registers/{Cas1}/device-key", null));

        async Task<HttpStatusCode> Exchange(Guid register, string key) =>
            (await Anonymous().PostAsJsonAsync("/api/v1/auth/register-token", new RegisterTokenRequest(register, key))).StatusCode;

        (await Exchange(Cas1, first.DeviceKey)).Should().Be(HttpStatusCode.Unauthorized, "rotation invalidates the previous key");
        (await Exchange(Cas1, "nrpos_wrong")).Should().Be(HttpStatusCode.Unauthorized);
        (await Exchange(DemoDataSeeder.Id("register:CAS2"), second.DeviceKey)).Should().Be(HttpStatusCode.Unauthorized);
        (await Exchange(Cas1, second.DeviceKey)).Should().Be(HttpStatusCode.OK);

        (await AdminClient().DeleteAsync($"/api/v1/registers/{Cas1}/device-key")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Exchange(Cas1, second.DeviceKey)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_token_endpoint_is_rate_limited()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            statuses.Add((await Anonymous().PostAsJsonAsync("/api/v1/auth/register-token", new RegisterTokenRequest(Cas1, "nrpos_x"))).StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
    }
}
