using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Newrest.Pos.Client.Core;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sales;
using Newrest.Pos.Client.Core.Sessions;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Devices.Badges;
using Newrest.Pos.Devices.Camera;
using Newrest.Pos.Devices.Display;
using Newrest.Pos.Devices.Printing;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Scenarios.Tests;

[CollectionDefinition(Name)]
public sealed class ScenarioCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = SqlServerFixture.CollectionName;
}

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string UserKey = "scenario-user-signing-key-0123456789-abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:PosDb", connectionString);
        builder.UseSetting("Authentication:Users:DevSigningKey", UserKey);
        builder.UseSetting("Authentication:Users:Audience", "newrest-pos-api");
        builder.UseSetting("Authentication:Registers:SigningKey", "scenario-register-signing-key-0123456789-ab");
        builder.UseSetting("Storage:RootPath", Path.Combine(Path.GetTempPath(), "pos-scenarios"));
        builder.UseSetting("Security:PinHashIterations", "1000");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }

    public HttpClient Admin()
    {
        var client = CreateClient();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "newrest-pos-dev",
            Audience = "newrest-pos-api",
            Subject = new ClaimsIdentity([new Claim("preferred_username", "admin@newrest.ma"), new Claim("roles", PosRoles.Admin)]),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(UserKey)), SecurityAlgorithms.HmacSha256),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public enum NetworkMode
{
    Up,
    Down,

    /// <summary>The request reaches the server but the response is lost (worst case for idempotency).</summary>
    LoseResponses,
}

/// <summary>Simulated network between the register and the server.</summary>
public sealed class NetworkSwitch : DelegatingHandler
{
    public NetworkMode Mode { get; set; } = NetworkMode.Up;

    public int Requests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (Mode == NetworkMode.Down)
        {
            throw new HttpRequestException("Network down (simulated)");
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (Mode == NetworkMode.LoseResponses && request.Method == HttpMethod.Post && !request.RequestUri!.AbsolutePath.Contains("register-token"))
        {
            response.Dispose();
            throw new HttpRequestException("Response lost (simulated)");
        }

        return response;
    }
}

/// <summary>A complete headless register: Client.Core services, SQLite file, simulated devices, switchable network.</summary>
public sealed class RegisterHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private RegisterHarness(ServiceProvider services, NetworkSwitch network, SimulatedReceiptPrinter printer, string folder)
    {
        _services = services;
        Network = network;
        Printer = printer;
        Folder = folder;
    }

    public NetworkSwitch Network { get; }

    public SimulatedReceiptPrinter Printer { get; }

    public string Folder { get; }

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public static async Task<RegisterHarness> CreateAsync(ApiFactory api, Guid registerId, RegisterOptions? options = null, string? folder = null,
        Action<IServiceCollection>? configure = null)
    {
        folder ??= Path.Combine(Path.GetTempPath(), "pos-register-" + Guid.NewGuid().ToString("N"));
        options ??= new RegisterOptions();
        options.DataFolder = folder;
        options.ReferenceSyncInterval = TimeSpan.FromHours(1);
        options.RequestTimeout = TimeSpan.FromSeconds(30);
        var network = new NetworkSwitch();
        var printer = new SimulatedReceiptPrinter(Path.Combine(folder, "receipts"));
        var collection = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddRegisterCore(options)
            .AddSingleton(api.CreateDefaultClient(network))
            .AddSingleton<IDeviceKeyStore>(new InMemoryDeviceKeyStore())
            .AddSingleton<IReceiptPrinter>(printer)
            .AddSingleton<ICashDrawer>(printer)
            .AddSingleton<ICustomerDisplay, SimulatedCustomerDisplay>()
            .AddSingleton<SimulatedBadgeReader>()
            .AddSingleton<IBadgeReader>(sp => sp.GetRequiredService<SimulatedBadgeReader>())
            .AddSingleton<IUiDispatcher, InlineDispatcher>()
            .AddSingleton<ICamera>(new SimulatedCamera());
        configure?.Invoke(collection);
        var services = collection.BuildServiceProvider();
        var harness = new RegisterHarness(services, network, printer, folder);

        var key = await (await api.Admin().PostAsync($"/api/v1/registers/{registerId}/device-key", null)).Content.ReadFromJsonAsync<DeviceKeyIssued>();
        await harness.Get<RegisterSetupService>().RegisterAsync(new Uri("http://localhost/"), registerId, key!.DeviceKey);
        return harness;
    }

    public async Task<LocalSessionContext> LoginAndOpenAsync(string operatorCode = "CAIS01", string pin = "1234", decimal openingFloat = 200m)
    {
        var login = Get<OperatorLoginService>();
        (await login.LoginAsync(operatorCode, pin)).Should().Be(LoginOutcome.Success);
        var sessions = Get<CashSessionService>();
        var session = await sessions.GetOpenSessionAsync() ?? await sessions.OpenAsync(login.Current!.Id, openingFloat);
        return new LocalSessionContext(login.Current!, session);
    }

    public async Task<Cart> CartAsync(Client.Data.LocalCashSession session, params string[] articleCodes)
    {
        var menu = (await Get<ReferenceCache>().GetMenusAsync(session.BusinessDate)).Should().ContainSingle().Subject;
        var articles = await Get<ReferenceCache>().GetArticlesAsync();
        var cart = new Cart();
        foreach (var code in articleCodes)
        {
            var item = menu.Items.Single(i => i.ArticleCode == code);
            cart.Add(item, articles[item.ArticleId].VatRate, articles[item.ArticleId].IsSubsidizable);
        }

        return cart;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed record LocalSessionContext(LoggedOperator Operator, Client.Data.LocalCashSession Session);
