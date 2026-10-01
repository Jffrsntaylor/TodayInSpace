using Microsoft.Extensions.Diagnostics.HealthChecks;
using TodayInSpace.Web.Health;

namespace TodayInSpace.Web.Tests
{
    public class HealthResponseTests
    {
        private static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries) =>
            new(entries.ToDictionary(e => e.Name, e => e.Entry), TimeSpan.Zero);

        private static HealthReportEntry Entry(HealthStatus status, string description,
            Exception? ex = null, Dictionary<string, object>? data = null) =>
            new(status, description, TimeSpan.Zero, ex, data);

        [Fact]
        public void Build_Healthy_ListsChecksAndLatestDigest()
        {
            var body = HealthResponse.Build(Report(
                ("storage", Entry(HealthStatus.Healthy, "latest.json is readable.")),
                ("freshness", Entry(HealthStatus.Healthy, "Latest digest is 12 hours old.",
                    data: new() { [FreshnessHealthCheck.LatestDigestKey] = "2026-10-01" }))));

            Assert.Equal("Healthy", body.Status);
            Assert.Equal("2026-10-01", body.LatestDigest);
            Assert.Equal(new[] { "storage", "freshness" }, body.Checks.Select(c => c.Name));
            Assert.Equal("Latest digest is 12 hours old.", body.Checks[1].Description);
        }

        [Fact]
        public void Build_WorstCheckSetsOverallStatus()
        {
            var body = HealthResponse.Build(Report(
                ("storage", Entry(HealthStatus.Healthy, "ok")),
                ("freshness", Entry(HealthStatus.Degraded, "Latest digest is 40 hours old."))));

            Assert.Equal("Degraded", body.Status);
            Assert.Equal("Degraded", body.Checks[1].Status);
        }

        [Fact]
        public void Build_StorageNotConfigured_HasNoLatestDigest()
        {
            var body = HealthResponse.Build(Report(
                ("storage", Entry(HealthStatus.Unhealthy, "Storage not configured.")),
                ("freshness", Entry(HealthStatus.Unhealthy, "No digest to check."))));

            Assert.Equal("Unhealthy", body.Status);
            Assert.Null(body.LatestDigest);
            Assert.Equal("Storage not configured.", body.Checks[0].Description);
        }

        [Fact]
        public void Build_CheckThatThrew_HidesExceptionText()
        {
            var secret = "AccountKey=abc123";
            var body = HealthResponse.Build(Report(
                ("storage", Entry(HealthStatus.Unhealthy, secret, new InvalidOperationException(secret)))));

            Assert.Equal("Check failed.", body.Checks[0].Description);
            Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(body));
        }
    }
}
