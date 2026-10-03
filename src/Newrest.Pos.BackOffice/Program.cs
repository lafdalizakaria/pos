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

var app = builder.Build();

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
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
