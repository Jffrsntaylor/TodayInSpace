using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Telemetry goes to Application Insights only when Azure sets the connection string,
// so local runs and CI skip it (same as the web app).
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services
        .AddApplicationInsightsTelemetryWorkerService()
        .ConfigureFunctionsApplicationInsights();

    // The worker SDK only forwards Warning and above by default. The daily run summary is
    // Information, and it's the line we want to query, so drop that default filter.
    builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
    {
        var defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName ==
            "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
        if (defaultRule != null)
            options.Rules.Remove(defaultRule);
    });
}

builder.Build().Run();
