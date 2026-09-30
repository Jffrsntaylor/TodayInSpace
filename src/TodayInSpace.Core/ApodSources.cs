using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TodayInSpace.Core
{
    // A day's picture as read from one of NASA's sources.
    public record ApodEntry(string Title, string Explanation, string ImageUrl, string Copyright, string Date, string ArticleUrl = "");

    // Checks and parsing for NASA's APOD sources.
    // After APOD moved to science.nasa.gov (Sept 2026), the api.nasa.gov endpoint sometimes returns
    // the site's generic title ("NASA Science") and the NASA logo instead of the real picture,
    // so its answers are sanity-checked, and NASA's own APOD RSS feed is used as a backup.
    public static class ApodSources
    {
        private static readonly HashSet<string> GenericTitles = new(StringComparer.OrdinalIgnoreCase)
        {
            "NASA Science", "APOD - NASA Science", "NASA", "APOD", "Astronomy Picture of the Day"
        };

        // True when the data looks like a real APOD entry rather than a placeholder.
        // imageUrl may be empty (video days); if present it must look like an APOD image.
        public static bool LooksLikeRealApod(string? title, string? explanation, string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(title) || GenericTitles.Contains(title.Trim()))
                return false;
            if (string.IsNullOrWhiteSpace(explanation))
                return false;
            if (string.IsNullOrWhiteSpace(imageUrl))
                return true;

            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
                return false;
            string path = uri.AbsolutePath;
            return path.Contains("apod", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("logo", StringComparison.OrdinalIgnoreCase);
        }

        // Finds the item for the given date in NASA's APOD RSS feed (science.nasa.gov/feed/apod-basic/).
        // Items link to ".../apod-2026-september-29-some-title/". Returns null if that day isn't in the feed.
        public static ApodEntry? ParseRssForDate(string? xml, DateOnly date)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return null;

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (System.Xml.XmlException) { return null; }

            string month = date.ToString("MMMM", CultureInfo.InvariantCulture).ToLowerInvariant();
            string slug = $"apod-{date.Year}-{month}-{date.Day}-";

            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
            {
                string link = Child(item, "link");
                if (!link.Contains(slug, StringComparison.OrdinalIgnoreCase))
                    continue;

                string title = ToPlainText(Child(item, "title"));
                string explanation = ToPlainText(Child(item, "explanation") is { Length: > 0 } e ? e : Child(item, "description"));
                string image = SizeForWeb(Child(item, "hdurl"));
                string copyright = ToPlainText(Child(item, "copyright") is { Length: > 0 } c ? c : Child(item, "credit"));

                if (!LooksLikeRealApod(title, explanation, image))
                    return null;

                return new ApodEntry(title, explanation, image, copyright, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), link);
            }
            return null;
        }

        // Reads the main media from an APOD article page on science.nasa.gov. Video days publish
        // og:video (e.g. an .mp4) with a snapshot in og:image; picture days have only og:image.
        public static (string VideoUrl, string ImageUrl) ParseArticleMedia(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return ("", "");
            return (MetaContent(html, "og:video:secure_url") is { Length: > 0 } v ? v : MetaContent(html, "og:video"),
                    MetaContent(html, "og:image"));
        }

        private static string MetaContent(string html, string property)
        {
            // Accepts either attribute order: property/name first or content first.
            string p = Regex.Escape(property);
            var m = Regex.Match(html, $@"<meta[^>]+(?:property|name)\s*=\s*[""']{p}[""'][^>]*content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(html, $@"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]*(?:property|name)\s*=\s*[""']{p}[""']", RegexOptions.IgnoreCase);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : "";
        }

        // Strips HTML tags and entities and collapses whitespace.
        public static string ToPlainText(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return "";
            // Block tags become spaces; inline tags (links, emphasis) are removed without adding gaps.
            string text = Regex.Replace(html, @"</?(p|br|div|li|ul|ol|h\d)\b[^>]*>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", "");
            text = WebUtility.HtmlDecode(text);
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        // NASA's asset server resizes on request; the feed's full-size images can be 4000px+.
        // Ask for a web-friendly size instead. Other URLs are returned unchanged.
        public static string SizeForWeb(string? url, int maxSize = 1600)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "";
            if (!uri.Host.Equals("assets.science.nasa.gov", StringComparison.OrdinalIgnoreCase))
                return url;
            return $"{uri.GetLeftPart(UriPartial.Path)}?w={maxSize}&h={maxSize}&fit=clip";
        }

        private static string Child(XElement item, string localName) =>
            item.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? "";
    }
}
