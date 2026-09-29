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
    }
}
