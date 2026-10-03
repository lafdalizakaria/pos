using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Newrest.Pos.BackOffice.Components;
using Newrest.Pos.Infrastructure;
using Newrest.Pos.Infrastructure.Persistence;
using Serilog;

// Phase 1: hosting skeleton. Entra ID authentication and business screens come in phase 2.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Newrest.Pos.BackOffice"));

builder.Services.AddPosInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<PosDbContext>("database", tags: ["ready"]);
builder.Services.AddLocalization();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// French UI by default; Arabic (RTL) is planned and must be added to the supported cultures.
string[] cultures = ["fr-MA", "fr-FR"];
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(cultures[0])
    .AddSupportedCultures(cultures)
    .AddSupportedUICultures(cultures));
CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(cultures[0]);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
