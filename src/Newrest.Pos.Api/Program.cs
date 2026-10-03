using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Newrest.Pos.Api;
using Newrest.Pos.Api.Endpoints;
using Newrest.Pos.Api.Security;
using Newrest.Pos.Application;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Contracts;
using Newrest.Pos.Infrastructure;
using Newrest.Pos.Infrastructure.Persistence;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// The database is NOT migrated here: use Newrest.Pos.Migrator.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Newrest.Pos.Api"));

builder.Services.AddPosInfrastructure(builder.Configuration);
builder.Services.AddPosApplication();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddPosAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.TokenRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddOpenApi(ApiVersion.V1);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PosDbContext>("database", tags: ["ready"]);

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("newrest-pos-api"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation());
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

var v1 = app.MapGroup(ApiVersion.BasePath);
v1.MapGet("/ping", (TimeProvider clock) => Results.Ok(new { status = "ok", serverTime = clock.GetUtcNow() }))
    .WithName("Ping").AllowAnonymous();
v1.MapAuthEndpoints();
v1.MapRegisterSyncEndpoints();

// Every management endpoint requires a back-office role; scopes and fine-grained roles are enforced by the use cases.
var backOffice = v1.MapGroup(string.Empty).RequireAuthorization(AuthenticationSetup.BackOfficePolicy);
backOffice.MapOrganizationEndpoints();
backOffice.MapCatalogEndpoints();
backOffice.MapMenuEndpoints();
backOffice.MapClientEndpoints();
backOffice.MapAccountEndpoints();
backOffice.MapSalesEndpoints();
backOffice.MapVisionEndpoints();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory-based tests.</summary>
public partial class Program;
