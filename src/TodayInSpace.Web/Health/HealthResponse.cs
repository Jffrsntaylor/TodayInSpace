using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TodayInSpace.Web.Health
{
    // The JSON body for /healthz. The endpoint is public, so it only ever shows each check's own
    // description: never exception text, which could include storage account details.
    public static class HealthResponse
    {
        public record CheckEntry(string Name, string Status, string Description);
        public record Body(string Status, IReadOnlyList<CheckEntry> Checks, string? LatestDigest);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public static Body Build(HealthReport report)
        {
            var checks = report.Entries
                .Select(e => new CheckEntry(
                    e.Key,
                    e.Value.Status.ToString(),
                    // A check that threw gets the framework's message, which may come from the exception.
                    e.Value.Exception == null ? e.Value.Description ?? "" : "Check failed."))
                .ToList();

            string? latestDigest = report.Entries.Values
                .Select(e => e.Data.TryGetValue(FreshnessHealthCheck.LatestDigestKey, out var d) ? d as string : null)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d));

            return new Body(report.Status.ToString(), checks, latestDigest);
        }

        public static Task WriteAsync(HttpContext context, HealthReport report)
        {
            // Monitors need the current answer every time, never a cached one.
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync(JsonSerializer.Serialize(Build(report), JsonOptions));
        }
    }
}
