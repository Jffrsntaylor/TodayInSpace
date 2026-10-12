using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace TodayInSpace.Web.Security
{
    // A per-visitor limit on the endpoints that take a location. /api/sky/visible does a 7-day pass search and can
    // call the National Weather Service for every new place, so someone looping over random coordinates could keep
    // the single-core web app busy and make NWS see a burst from our IP. A real visitor needs a handful of calls.
    public static class LocationRateLimit
    {
        public const string PolicyName = "location";
        public const int RequestsPerMinute = 30;

        // Visitors without an address (shouldn't happen behind App Service) share one bucket rather than going unlimited.
        public const string SharedPartition = "unknown";

        public static void Configure(RateLimiterOptions options)
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PolicyName, Partition);
            options.OnRejected = (context, _) =>
            {
                SetRetryAfter(context.HttpContext.Response, context.Lease);
                return ValueTask.CompletedTask;
            };
        }

        // Only the IP is used as the key, and nothing here logs it, so an address is never recorded next to coordinates.
        public static RateLimitPartition<string> Partition(HttpContext context) =>
            RateLimitPartition.GetTokenBucketLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? SharedPartition,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = RequestsPerMinute,
                    TokensPerPeriod = RequestsPerMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    // No queue: a rejected lookup fails fast and the page says to try again.
                    QueueLimit = 0,
                    AutoReplenishment = true
                });

        public static void SetRetryAfter(HttpResponse response, RateLimitLease lease)
        {
            var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                : 60;
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }
    }
}
