using System.Text.RegularExpressions;

namespace TodayInSpace.Core
{
    // Naming rules for archived APOD images in blob storage.
    // One image per day, named by date: "2026-09-28.jpg".
    public static class ApodImageNaming
    {
        public const string DefaultExtension = ".jpg";

        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        };

        private static readonly Regex DateRegex = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
        private static readonly Regex BlobNameRegex = new(@"^\d{4}-\d{2}-\d{2}\.(jpg|jpeg|png|gif|webp)$", RegexOptions.Compiled);

        // Builds the blob name for a day's image, keeping the source file's extension
        // when it's a known image type. Returns null if the date or URL is unusable.
        public static string? GetBlobName(string? date, string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(date) || !DateRegex.IsMatch(date))
                return null;
            if (string.IsNullOrWhiteSpace(imageUrl) ||
                !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;

            string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (!ContentTypes.ContainsKey(ext))
                ext = DefaultExtension;

            return date + ext;
        }

        // True only for names this app creates, so the web endpoint can't be used
        // to read arbitrary blobs.
        public static bool IsValidBlobName(string? name)
        {
            return !string.IsNullOrEmpty(name) && BlobNameRegex.IsMatch(name);
        }

        public static string GetContentType(string blobName)
        {
            string ext = Path.GetExtension(blobName);
            return ContentTypes.TryGetValue(ext, out var type) ? type : "application/octet-stream";
        }
    }
}
