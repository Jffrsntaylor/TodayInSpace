using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using TodayInSpace.Web.Controllers;
using TodayInSpace.Web.Security;

namespace TodayInSpace.Web.Tests
{
    // The per-visitor limit on the location endpoints, run through the real rate limiting middleware.
    public class LocationRateLimitTests
    {
        private static RequestDelegate Pipeline(bool limited = true)
        {
            var services = new ServiceCollection()
                .AddLogging()
                .AddRateLimiter(LocationRateLimit.Configure)
                .BuildServiceProvider();

            var app = new ApplicationBuilder(services);
            // Stands in for routing: the middleware reads the policy from the matched endpoint's metadata.
            app.Use((context, next) =>
            {
                var metadata = limited
                    ? new EndpointMetadataCollection(new EnableRateLimitingAttribute(LocationRateLimit.PolicyName))
                    : EndpointMetadataCollection.Empty;
                context.SetEndpoint(new Endpoint(null, metadata, "test"));
                return next(context);
            });
            app.UseRateLimiter();
            app.Run(context => { context.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; });
            return app.Build();
        }

        private static async Task<HttpContext> SendAsync(RequestDelegate pipeline, string? ip)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = ip == null ? null : IPAddress.Parse(ip);
            await pipeline(context);
            return context;
        }

        [Fact]
        public async Task ThirtyFirstRequestInAMinute_Gets429WithRetryAfter()
        {
            var pipeline = Pipeline();
            for (int i = 0; i < LocationRateLimit.RequestsPerMinute; i++)
                Assert.Equal(200, (await SendAsync(pipeline, "203.0.113.5")).Response.StatusCode);

            var rejected = await SendAsync(pipeline, "203.0.113.5");

            Assert.Equal(429, rejected.Response.StatusCode);
            var retryAfter = int.Parse(rejected.Response.Headers.RetryAfter.ToString());
            Assert.InRange(retryAfter, 1, 60);
        }

        [Fact]
        public async Task EachVisitorHasTheirOwnBucket()
        {
            var pipeline = Pipeline();
            for (int i = 0; i <= LocationRateLimit.RequestsPerMinute; i++)
                await SendAsync(pipeline, "203.0.113.5");

            Assert.Equal(200, (await SendAsync(pipeline, "198.51.100.7")).Response.StatusCode);
        }

        [Fact]
        public async Task MissingAddress_SharesOneBucket()
        {
            var pipeline = Pipeline();
            for (int i = 0; i < LocationRateLimit.RequestsPerMinute; i++)
                await SendAsync(pipeline, null);

            Assert.Equal(429, (await SendAsync(pipeline, null)).Response.StatusCode);
        }

        [Fact]
        public async Task EndpointsWithoutThePolicy_AreNotLimited()
        {
            var pipeline = Pipeline(limited: false);
            for (int i = 0; i < LocationRateLimit.RequestsPerMinute * 2; i++)
                Assert.Equal(200, (await SendAsync(pipeline, "203.0.113.5")).Response.StatusCode);
        }

        // Only the two endpoints that take a location carry the policy; the aurora, TLE and other feeds don't.
        [Fact]
        public void PolicyIsOnTheLocationEndpointsOnly()
        {
            var limited = typeof(SkyController).Assembly.GetTypes()
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.GetCustomAttribute<EnableRateLimitingAttribute>() != null
                        || t.GetCustomAttribute<EnableRateLimitingAttribute>() != null)
                    .Select(m => t.Name + "." + m.Name))
                .OrderBy(n => n)
                .ToArray();

            Assert.Equal(new[] { "LocationController.GetZip", "SkyController.GetVisible" }, limited);
        }
    }
}
