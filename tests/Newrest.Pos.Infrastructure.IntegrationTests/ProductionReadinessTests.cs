using Microsoft.Extensions.Configuration;
using Newrest.Pos.Infrastructure.Hosting;

namespace Newrest.Pos.Infrastructure.IntegrationTests;

public class ProductionReadinessTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> ValidApi() => new()
    {
        ["ConnectionStrings:PosDb"] = "Server=sql.newrest.ma;Database=NewrestPos;User Id=pos_api;Password=x;Encrypt=True",
        ["Storage:RootPath"] = "/data",
        ["Security:PinHashIterations"] = "600000",
        ["Archive:SigningKeyPem"] = "-----BEGIN PRIVATE KEY-----",
        ["Authentication:Registers:SigningKey"] = new string('k', 48),
        ["Authentication:Users:Authority"] = "https://login.microsoftonline.com/t/v2.0",
        ["Authentication:Users:Audience"] = "api://newrest-pos",
        ["Supervision:WebhookUrl"] = "https://hooks",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://otel:4317",
    };

    [Fact]
    public void A_complete_production_configuration_passes()
    {
        var report = ProductionReadiness.Check(Config(ValidApi()), ServerHost.Api);
        report.Errors.Should().BeEmpty();
        report.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Development_settings_and_missing_secrets_are_refused_all_at_once()
    {
        var values = ValidApi();
        values["ConnectionStrings:PosDb"] = "Server=localhost;Database=NewrestPos;User Id=sa;Password=x;TrustServerCertificate=true";
        values["Archive:SigningKeyPem"] = null;
        values["Archive:AllowEphemeralKey"] = "true";
        values["Authentication:Registers:SigningKey"] = "short";
        values["Authentication:Users:DevSigningKey"] = "dev";
        values["Security:PinHashIterations"] = "1000";
        values["Supervision:WebhookUrl"] = null;

        var report = ProductionReadiness.Check(Config(values), ServerHost.Api);
        report.Errors.Should().HaveCount(7);
        report.Warnings.Should().ContainSingle().Which.Should().Contain("WebhookUrl");
        FluentActions.Invoking(() => ProductionReadiness.EnsureReady(Config(values), ServerHost.Api)).Should().Throw<InvalidOperationException>()
            .WithMessage("*'sa'*").WithMessage("*SigningKey*");
    }

    [Fact]
    public void Back_office_needs_entra_id_and_no_dev_login()
    {
        var values = ValidApi();
        values["Authentication:DevLoginEnabled"] = "true";
        var errors = ProductionReadiness.Check(Config(values), ServerHost.BackOffice).Errors;
        errors.Should().Contain(e => e.Contains("ClientSecret")).And.Contain(e => e.Contains("DevLoginEnabled"));
    }
}
