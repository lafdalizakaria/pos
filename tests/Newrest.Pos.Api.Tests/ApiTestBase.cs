using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Infrastructure.Seeding;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = SqlServerFixture.CollectionName;
}

public sealed class ApiFactory(string connectionString, string storageRoot) : WebApplicationFactory<Program>
{
    public const string UserKey = "test-user-signing-key-0123456789-abcdefghij";
    public const string RegisterKey = "test-register-signing-key-0123456789-abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting (not ConfigureAppConfiguration): Program reads configuration while registering services.
        builder.UseSetting("ConnectionStrings:PosDb", connectionString);
        builder.UseSetting("Authentication:Users:DevSigningKey", UserKey);
        builder.UseSetting("RateLimits:RegisterTokenPerMinute", "20");
        builder.UseSetting("Authentication:Users:Audience", "newrest-pos-api");
        builder.UseSetting("Authentication:Registers:SigningKey", RegisterKey);
        builder.UseSetting("Storage:RootPath", storageRoot);
        builder.UseSetting("Security:PinHashIterations", "1000");
        builder.UseSetting("Supervision:JobsEnabled", "false");
        builder.UseSetting("Archive:AllowEphemeralKey", "true");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }
}

/// <summary>Each test class gets its own seeded database and API host.</summary>
[Collection(ApiCollection.Name)]
public abstract class ApiTestBase(SqlServerFixture fixture) : IAsyncLifetime
{
    public const string Admin = "admin@newrest.ma";
    public const string CasaManager = "manager.casa@newrest.ma";
    public const string NfmsAccountant = "compta.nfms@newrest.ma";
    public const string Viewer = "lecture@newrest.ma";

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected static readonly Guid Nfms = DemoDataSeeder.Id("company:NFMS");
    protected static readonly Guid Nms = DemoDataSeeder.Id("company:NMS");
    protected static readonly Guid CasaSite = DemoDataSeeder.Id("site:CAS-SM");
    protected static readonly Guid TangerSite = DemoDataSeeder.Id("site:TNG-TFZ");
    protected static readonly Guid CasaSelf = DemoDataSeeder.Id("pos:CAS-SELF");
    protected static readonly Guid TngSnack = DemoDataSeeder.Id("pos:TNG-SNACK");
    protected static readonly Guid KenSelf = DemoDataSeeder.Id("pos:KEN-SELF");
    protected static readonly Guid Atlas = DemoDataSeeder.Id("client:ATLAS");
    protected static readonly Guid Sahara = DemoDataSeeder.Id("client:SAHARA");
    protected static readonly Guid AtlasContract = DemoDataSeeder.Id("contract:ATLAS");
    protected static readonly Guid CouscousViande = DemoDataSeeder.Id("article:CSC-VND");
    protected static readonly Guid CouscousPoulet = DemoDataSeeder.Id("article:CSC-PLT");
    protected static readonly Guid Cafe = DemoDataSeeder.Id("article:CAF-EXP");

    private string _storage = null!;

    protected ApiFactory Factory { get; private set; } = null!;

    protected TestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await fixture.CreateDatabaseAsync(seedDemo: true);
        _storage = Path.Combine(Path.GetTempPath(), "pos-tests-" + Guid.NewGuid().ToString("N"));
        Factory = new ApiFactory(Database.ConnectionString, _storage);

        // Scopes used by the tests: a Casablanca site manager and an NFMS-wide accountant.
        var admin = AsUser(Admin, PosRoles.Admin);
        (await admin.PostAsJsonAsync("/api/v1/access-scopes", new AccessScopeCreate(CasaManager, Nfms, CasaSite))).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/v1/access-scopes", new AccessScopeCreate(NfmsAccountant, Nfms, null))).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/v1/access-scopes", new AccessScopeCreate(Viewer, Nfms, null))).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    protected HttpClient Anonymous() => Factory.CreateClient();

    protected HttpClient AsUser(string userName, params string[] roles)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken(userName, roles));
        return client;
    }

    protected HttpClient AdminClient() => AsUser(Admin, PosRoles.Admin);

    protected HttpClient ManagerClient() => AsUser(CasaManager, PosRoles.Manager);

    protected HttpClient AccountantClient() => AsUser(NfmsAccountant, PosRoles.Accountant);

    protected HttpClient ViewerClient() => AsUser(Viewer, PosRoles.Viewer);

    protected static string UserToken(string userName, string[] roles, string? key = null, DateTime? expires = null)
    {
        var claims = new List<Claim> { new("preferred_username", userName), new("sub", userName) };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "newrest-pos-dev",
            Audience = "newrest-pos-api",
            Subject = new ClaimsIdentity(claims),
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-20),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? ApiFactory.UserKey)),
                SecurityAlgorithms.HmacSha256),
        });
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    protected static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
