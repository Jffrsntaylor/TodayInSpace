namespace TodayInSpace.Web.Helpers
{
    // Pure logic for interpreting space-weather values.
    // Pulled out of the Razor view so it can be unit tested.
    public static class SpaceWeatherHelper
    {
        // Maps a Kp index (0-9) to a severity category used for color-coding.
        // Returns "info" for null or out-of-range values (the "no data" case).
        public static string GetKpSeverity(int? kp)
        {
            if (kp == null || kp < 0 || kp > 9)
                return "info";

            if (kp < 4) return "calm";    // 0-3
            if (kp < 6) return "watch";   // 4-5
            return "alert";               // 6-9
        }

        // Short human description of a Kp value.
        public static string GetKpDescription(int? kp)
        {
            if (kp == null || kp < 0 || kp > 9)
                return "unavailable";

            if (kp < 3) return "quiet";
            if (kp < 5) return "unsettled";
            if (kp < 7) return "minor storm";
            return "severe storm";
        }

        // Maps an aurora-chance label to a severity category.
        // Handles null/empty/unknown by returning "info".
        public static string GetAuroraSeverity(string? chance)
        {
            if (string.IsNullOrWhiteSpace(chance))
                return "info";

            switch (chance.Trim().ToUpper())
            {
                case "UNLIKELY":
                case "UNAVAILABLE":
                    return "info";
                case "POSSIBLE":
                    return "watch";
                case "LIKELY":
                case "STRONG":
                    return "alert";
                default:
                    return "info";   // unknown label -> safe default
            }
        }

        // Converts a Kp value into a bar height percentage (for the forecast bars).
        // Clamps to a minimum of 8% so even Kp 0 shows a sliver, and caps at 100%.
        public static int GetForecastBarPercent(int? kp)
        {
            if (kp == null || kp < 0) return 8;
            int pct = (int)(kp.Value / 9.0 * 100);
            if (pct < 8) return 8;
            if (pct > 100) return 100;
            return pct;
        }
    }
}
