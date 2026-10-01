using Microsoft.Extensions.Diagnostics.HealthChecks;
using TodayInSpace.Core;
using TodayInSpace.Web.Services;

namespace TodayInSpace.Web.Health
{
    // Did the daily function publish recently? NASA outages have stopped it silently before.
    public class FreshnessHealthCheck : IHealthCheck
    {
        // Key in the result data that HealthResponse reads to show the latest digest's date.
        public const string LatestDigestKey = "latestDigest";

        private readonly DigestService _digests;

        public FreshnessHealthCheck(DigestService digests)
        {
            _digests = digests;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var latest = _digests.IsConfigured ? await _digests.GetLatestAsync() : null;
            if (latest == null)
                return HealthCheckResult.Unhealthy("No digest to check.");

            var data = new Dictionary<string, object> { [LatestDigestKey] = latest.Date ?? "" };
            var (level, age) = DigestFreshness.Evaluate(latest.Date, DateTimeOffset.UtcNow);

            if (age == null)
                return HealthCheckResult.Unhealthy("Latest digest has no valid date.", data: data);

            string description = $"Latest digest is {age.Value.TotalHours:0} hours old.";
            return level switch
            {
                FreshnessLevel.Fresh => HealthCheckResult.Healthy(description, data),
                FreshnessLevel.Stale => HealthCheckResult.Degraded(description, data: data),
                _ => HealthCheckResult.Unhealthy(description, data: data)
            };
        }
    }
}
