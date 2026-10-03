using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Newrest.Pos.Contracts;
using Newrest.Pos.Infrastructure;
using Newrest.Pos.Infrastructure.Persistence;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// Phase 1: hosting skeleton (logging, telemetry, health checks, OpenAPI).
// Business endpoints and authentication are delivered in phase 2. The database is NOT migrated here:
// use Newrest.Pos.Migrator.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Newrest.Pos.Api"));

builder.Services.AddPosInfrastructure(builder.Configuration);
builder.Services.AddOpenApi(ApiVersion.V1);
builder.Services.AddProblemDetails();
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
app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

var v1 = app.MapGroup(ApiVersion.BasePath);
v1.MapGet("/ping", (TimeProvider clock) => Results.Ok(new { status = "ok", serverTime = clock.GetUtcNow() }))
    .WithName("Ping");

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory-based tests.</summary>
public partial class Program;

