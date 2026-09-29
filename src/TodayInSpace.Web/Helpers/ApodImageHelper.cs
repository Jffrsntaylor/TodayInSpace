using TodayInSpace.Core;
using TodayInSpace.Web.Models;

namespace TodayInSpace.Web.Helpers
{
    public static class ApodImageHelper
    {
        // Prefer our archived copy (served from /images/...); fall back to NASA's URL
        // for digests that haven't been backfilled. Returns null when there's no image
        // (e.g. APOD was a video that day).
        public static string? GetDisplayUrl(ApodInfo? apod)
        {
            if (apod == null)
                return null;

            if (ApodImageNaming.IsValidBlobName(apod.ImageBlob))
                return "/images/" + apod.ImageBlob;

            return string.IsNullOrWhiteSpace(apod.ImageUrl) ? null : apod.ImageUrl;
        }

        // When NASA's API was down during the daily update, the digest reuses the last good picture.
        // Returns that picture's original date so the page can say so; null when the picture is current.
        public static DateTime? GetCarriedOverDate(DigestModel? digest)
        {
            string? apodDate = digest?.Apod?.Date;
            if (string.IsNullOrWhiteSpace(apodDate) || string.IsNullOrWhiteSpace(digest!.Date) || apodDate == digest.Date)
                return null;

            return DateTime.TryParseExact(apodDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }
    }
}
