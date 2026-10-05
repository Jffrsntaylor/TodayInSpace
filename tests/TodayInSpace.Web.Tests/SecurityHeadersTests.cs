using Microsoft.AspNetCore.Http;
using TodayInSpace.Web.Security;

namespace TodayInSpace.Web.Tests
{
    public class SecurityHeadersTests
    {
        private static async Task<(HttpContext Context, bool NextCalled)> RunAsync()
        {
            var context = new DefaultHttpContext();
            bool nextCalled = false;
            var middleware = new SecurityHeadersMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
            await middleware.InvokeAsync(context);
            return (context, nextCalled);
        }

        [Theory]
        [InlineData("X-Content-Type-Options", "nosniff")]
        [InlineData("Referrer-Policy", "strict-origin-when-cross-origin")]
        [InlineData("X-Frame-Options", "DENY")]
        [InlineData("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()")]
        [InlineData("Cross-Origin-Opener-Policy", "same-origin")]
        public async Task AddsHeader(string name, string expected)
        {
            var (context, _) = await RunAsync();

            Assert.Equal(expected, context.Response.Headers[name].ToString());
        }

        [Fact]
        public async Task Csp_IsReportOnly()
        {
            var headers = (await RunAsync()).Context.Response.Headers;

            Assert.False(headers.ContainsKey("Content-Security-Policy"));
            Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy,
                headers["Content-Security-Policy-Report-Only"].ToString());
        }

        [Fact]
        public void Csp_BlocksFramingAndInlineScripts()
        {
            var directives = SecurityHeadersMiddleware.ContentSecurityPolicy.Split("; ");

            Assert.Contains("frame-ancestors 'none'", directives);
            Assert.Contains("script-src 'self'", directives);
            Assert.Contains("object-src 'none'", directives);
        }

        [Fact]
        public async Task CallsNext()
        {
            var (_, nextCalled) = await RunAsync();

            Assert.True(nextCalled);
        }
    }
}
