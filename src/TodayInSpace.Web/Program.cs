using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TodayInSpace.Web.Health;
using TodayInSpace.Web.Helpers;
using TodayInSpace.Web.Security;
using TodayInSpace.Web.Services;
using TodayInSpace.Web.Sky;

var builder = WebApplication.CreateBuilder(args);

// Don't advertise the web server in every response. (IIS on Azure may still add its own.)
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddControllersWithViews();
builder.Services.AddScoped<DigestService>();

// Live Sky map feeds (ISS orbit data + NOAA aurora forecast), fetched server-side and cached.
builder.Services.AddMemoryCache();
// People in space comes from storage; the function app fetches it from LL2 on a timer.
builder.Services.AddSingleton<IPeopleStore, BlobPeopleStore>();
// So do the launches (next launch + this year's count).
builder.Services.AddSingleton<ILaunchStore, BlobLaunchStore>();
// And the satellite counts, so the web app never downloads CelesTrak's 7 MB catalogue itself.
builder.Services.AddSingleton<ISatelliteStore, BlobSatelliteStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FlagIcons>();
builder.Services.AddHttpClient<SkyDataService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TodayInSpace/1.0 (+https://github.com/Jffrsntaylor/TodayInSpace)");
});

// "Your sky this week": zip code locations (loaded once) and visible passes, cached per rounded location.
builder.Services.AddSingleton<ZipLookup>();
builder.Services.AddSingleton<VisibleSkyCache>();
// Place names and cloud cover from the National Weather Service. NWS asks for a User-Agent that identifies the app.
// The request URLs contain the (rounded) location, so HttpClient's own request logging is turned off for this client.
builder.Services.AddSingleton<NwsCache>();
builder.Services.AddHttpClient<NwsWeatherService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TodayInSpace/1.0 (+https://space.evergreensystemswa.com)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
}).RemoveAllLoggers();
builder.Services.AddScoped<VisibleSkyService>();

// The Earth snapshot PNG for widgets and link previews. The two world maps are decoded once, on first use.
builder.Services.AddSingleton(sp => EarthSnapshotRenderer.FromWebRoot(sp.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider));
builder.Services.AddSingleton<EarthSnapshotCache>();
builder.Services.AddScoped<EarthSnapshotService>();

// /healthz: can we read storage, and did the daily function publish recently?
builder.Services.AddHealthChecks()
    .AddCheck<StorageHealthCheck>("storage")
    .AddCheck<FreshnessHealthCheck>("freshness");

// Telemetry goes to Application Insights only when Azure sets the connection string.
// Local runs and CI don't have it, so they skip this entirely.
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddApplicationInsightsTelemetry();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Before routing and static assets, so every response carries the headers.
app.UseSecurityHeaders();

app.UseHttpsRedirection();
app.UseRouting();

app.MapStaticAssets();

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = HealthResponse.WriteAsync,
    // Degraded (a missed run) still serves pages, so only Unhealthy returns 503.
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
