using Microsoft.Extensions.Diagnostics.HealthChecks;
using TodayInSpace.Web.Services;

namespace TodayInSpace.Web.Health
{
    // Can the site read latest.json? If not, every page is empty.
    public class StorageHealthCheck : IHealthCheck
    {
        private readonly DigestService _digests;

        public StorageHealthCheck(DigestService digests)
        {
            _digests = digests;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (!_digests.IsConfigured)
                return HealthCheckResult.Unhealthy("Storage not configured.");

            // DigestService already swallows storage errors, so the reason never reaches the response.
            return await _digests.GetLatestAsync() != null
                ? HealthCheckResult.Healthy("latest.json is readable.")
                : HealthCheckResult.Unhealthy("latest.json is missing or unreadable.");
        }
    }
}
