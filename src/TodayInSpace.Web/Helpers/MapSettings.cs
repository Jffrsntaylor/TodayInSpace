using Microsoft.Extensions.Configuration;

namespace TodayInSpace.Web.Helpers
{
    public static class MapSettings
    {
        // CARTO's basemap key (Map:CartoKey, or Map__CartoKey as an app setting). It ends up in the page on
        // purpose: tile keys are always visible to the browser, so it's restricted by domain in CARTO instead.
        // Empty when unset, and the map then runs without tiles rather than showing "API KEY REQUIRED".
        public static string CartoKey(IConfiguration config) => config["Map:CartoKey"]?.Trim() ?? "";
    }
}
