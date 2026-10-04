using System.Globalization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Newrest.Pos.Application;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Catalog;
using Newrest.Pos.BackOffice.Components;
using Newrest.Pos.BackOffice.Security;
using Newrest.Pos.Infrastructure;
using Newrest.Pos.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var productionWarnings = builder.Environment.IsProduction()
    ? Newrest.Pos.Infrastructure.Hosting.ProductionReadiness.EnsureReady(builder.Configuration, Newrest.Pos.Infrastructure.Hosting.ServerHost.BackOffice)
    : [];

builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Newrest.Pos.BackOffice"));

builder.Services.AddPosInfrastructure(builder.Configuration);
builder.Services.AddPosApplication();
builder.Services.AddScoped<UserPrincipalHolder>();
builder.Services.AddScoped<ICurrentUser, BackOfficeCurrentUser>();
builder.Services.AddScoped<BackOfficeRunner>();
builder.Services.AddScoped<CircuitHandler, UserCircuitHandler>();
builder.Services.AddBackOfficeAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddHealthChecks().AddDbContextCheck<PosDbContext>("database", tags: ["ready"]);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
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
    headers["Referrer-Policy"] = "same-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // Blazor Server: scripts from this origin only, WebSocket to this origin, Entra ID for the sign-in form posts.
    headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
                                    + "connect-src 'self' wss: ws:; frame-ancestors 'none'; base-uri 'self'; object-src 'none'; "
                                    + "form-action 'self' https://login.microsoftonline.com";
    await next(context);
});

// French UI by default; Arabic (RTL) is planned and must be added to the supported cultures.
string[] cultures = ["fr-MA", "fr-FR"];
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(cultures[0])
    .AddSupportedCultures(cultures)
    .AddSupportedUICultures(cultures));
CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(cultures[0]);
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(cultures[0]);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.Use((context, next) =>
{
    // Plain HTTP requests (prerender, file endpoints): expose the request user to the use cases.
    context.RequestServices.GetRequiredService<UserPrincipalHolder>().Principal = context.User;
    return next(context);
});
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapAccountEndpoints();
app.MapGet("/photos/{id:guid}", async (Guid id, CatalogService catalog, CancellationToken ct) =>
    await catalog.OpenPhotoAsync(id, ct) is { } photo ? Results.Stream(photo.Content, photo.ContentType) : Results.NotFound());
app.MapGet("/archives/{id:guid}", async (Guid id, Newrest.Pos.Application.Compliance.ArchiveService archives, CancellationToken ct) =>
    await archives.OpenAsync(id, ct) is { } file ? Results.File(file.Content, "application/zip", file.FileName) : Results.NotFound());
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
