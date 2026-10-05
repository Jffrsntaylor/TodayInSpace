namespace TodayInSpace.Web.Security
{
    // Adds the browser security headers that scanners like securityheaders.com grade on.
    // The site has no logins or user data, so this is defense in depth. It runs early in the
    // pipeline so every response gets them: pages, /images/*, static files and /healthz.
    public class SecurityHeadersMiddleware
    {
        // Report-Only for now: the browser logs violations to the console but blocks nothing,
        // so visitors can't be broken by a source we missed. Flip to enforcing once the live
        // console is clean.
        public const string CspHeaderName = "Content-Security-Policy-Report-Only";

        public static readonly string ContentSecurityPolicy = string.Join("; ",
            "default-src 'self'",
            "script-src 'self'",
            // Views still use style= attributes, and the page fonts come from Google Fonts.
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
            "font-src 'self' https://fonts.gstatic.com",
            // NASA images on fallback days, YouTube/Vimeo thumbnails, CARTO map tiles.
            "img-src 'self' data: blob: https:",
            // NASA .mp4 files on video days.
            "media-src 'self' https://*.nasa.gov",
            // APOD video embeds.
            "frame-src https://www.youtube-nocookie.com https://player.vimeo.com",
            // The globe and map only call our own /api/sky endpoints.
            "connect-src 'self'",
            // globe.gl spins up workers from blob: URLs.
            "worker-src 'self' blob:",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            // Modern replacement for X-Frame-Options: nobody may frame this site.
            "frame-ancestors 'none'");

        // The zip code feature may need geolocation=(self) here later.
        public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

        private readonly RequestDelegate _next;

        public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

        public Task InvokeAsync(HttpContext context)
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers.XFrameOptions = "DENY";
            headers["Permissions-Policy"] = PermissionsPolicy;
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers[CspHeaderName] = ContentSecurityPolicy;

            return _next(context);
        }
    }

    public static class SecurityHeadersExtensions
    {
        public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
            app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
