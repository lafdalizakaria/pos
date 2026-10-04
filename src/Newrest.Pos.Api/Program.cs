using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Newrest.Pos.Api;
using Newrest.Pos.Api.Endpoints;
using Newrest.Pos.Api.Operations;
using Newrest.Pos.Api.Security;
using Newrest.Pos.Application;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Operations;
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
var productionWarnings = builder.Environment.IsProduction()
    ? Newrest.Pos.Infrastructure.Hosting.ProductionReadiness.EnsureReady(builder.Configuration, Newrest.Pos.Infrastructure.Hosting.ServerHost.Api)
    : [];

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
        // Per IP: every register of a site usually shares one public address (NAT) and authenticates at the same time
        // (opening, restart). Device keys are 256-bit random: the limit only curbs abuse, it is not the protection.
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimits:RegisterTokenPerMinute", 300),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});
var supervision = builder.Configuration.GetSection("Supervision").Get<SupervisionOptions>() ?? new SupervisionOptions();
builder.Services.AddSingleton(supervision);
builder.Services.AddSingleton(supervision.Thresholds);
builder.Services.AddHttpClient(nameof(SupervisionWorker), c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<SupervisionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SupervisionWorker>());
builder.Services.AddOpenApi(ApiVersion.V1);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PosDbContext>("database", tags: ["ready"]);

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("newrest-pos-api"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddMeter(PosMetrics.MeterName));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    // Behind the reverse proxy / load balancer (TLS terminated there): trust X-Forwarded-For/Proto from the known proxy network only.
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

var app = builder.Build();
foreach (var warning in productionWarnings)
{
    app.Logger.LogWarning("Production configuration: {Warning}", warning);
}

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    headers.CacheControl = context.Request.Path.StartsWithSegments("/api") ? "no-store" : headers.CacheControl;
    await next(context);
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

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
backOffice.MapSupervisionEndpoints();
backOffice.MapComplianceEndpoints();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory-based tests.</summary>
public partial class Program;
