using System.Net;
using Microsoft.AspNetCore.Mvc;
using PocketLedger;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("PocketLedger:Port", 5111);
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
var configuredPath = builder.Configuration["PocketLedger:DataPath"];
var dataFile = Path.GetFullPath(configuredPath ?? Path.Combine("App_Data", "pocketledger.db"), builder.Environment.ContentRootPath);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new LedgerStore(dataFile));
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

var app = builder.Build();
app.Services.GetRequiredService<LedgerStore>().Initialize();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error");
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
    await next();
});
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new("en-US", "ru-RU"),
    SupportedCultures = [System.Globalization.CultureInfo.GetCultureInfo("en-US")],
    SupportedUICultures = [Display.Russian],
    RequestCultureProviders = []
});
app.UseStaticFiles();
app.UseRouting();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (!context.Response.Headers.ContainsKey("Cache-Control"))
            context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    await next();
});
app.MapControllers();
app.Run();
