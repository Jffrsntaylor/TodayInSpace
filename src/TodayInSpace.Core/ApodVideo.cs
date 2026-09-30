using System.Text.RegularExpressions;

namespace TodayInSpace.Core
{
    // How to show a video day's media on the page.
    //   Iframe: an embeddable player page (YouTube / Vimeo)
    //   File:   a direct video file for a <video> tag (NASA-hosted .mp4 etc.)
    public enum VideoKind { Iframe, File }

    public record VideoEmbed(VideoKind Kind, string Src);

    // Turns a video URL from NASA into something safe to embed. Only known hosts are allowed,
    // so a bad or unexpected URL in the data can never put an arbitrary page in an iframe.
    public static class ApodVideo
    {
        private static readonly Regex YouTubeId = new(@"^[A-Za-z0-9_-]{6,20}$", RegexOptions.Compiled);
        private static readonly Regex VimeoId = new(@"^\d{3,12}$", RegexOptions.Compiled);
        private static readonly string[] FileExtensions = { ".mp4", ".webm", ".mov", ".m4v" };

        public static VideoEmbed? ToEmbed(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return null;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                return null;

            string host = uri.Host.ToLowerInvariant();
            string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            // YouTube: /embed/ID, /watch?v=ID, /shorts/ID, youtu.be/ID
            if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtube-nocookie.com" or "www.youtube-nocookie.com" or "youtu.be")
            {
                string? id = null;
                if (host == "youtu.be" && segments.Length >= 1) id = segments[0];
                else if (segments.Length >= 2 && (segments[0] == "embed" || segments[0] == "shorts")) id = segments[1];
                else if (segments.Length == 1 && segments[0] == "watch") id = QueryValue(uri.Query, "v");

                return id != null && YouTubeId.IsMatch(id)
                    ? new VideoEmbed(VideoKind.Iframe, $"https://www.youtube-nocookie.com/embed/{id}?rel=0")
                    : null;
            }

            // Vimeo: player.vimeo.com/video/ID or vimeo.com/ID
            if (host is "vimeo.com" or "www.vimeo.com" or "player.vimeo.com")
            {
                string? id = host == "player.vimeo.com"
                    ? (segments.Length >= 2 && segments[0] == "video" ? segments[1] : null)
                    : (segments.Length >= 1 ? segments[0] : null);

                return id != null && VimeoId.IsMatch(id)
                    ? new VideoEmbed(VideoKind.Iframe, $"https://player.vimeo.com/video/{id}")
                    : null;
            }

            // NASA-hosted video files (science.nasa.gov assets, old apod.nasa.gov)
            if ((host == "nasa.gov" || host.EndsWith(".nasa.gov", StringComparison.Ordinal)) &&
                FileExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                return new VideoEmbed(VideoKind.File, "https://" + uri.Host + uri.PathAndQuery);
            }

            return null;
        }

        private static string? QueryValue(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0] == key)
                    return Uri.UnescapeDataString(kv[1]);
            }
            return null;
        }
    }
}
