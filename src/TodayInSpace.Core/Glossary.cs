namespace TodayInSpace.Core
{
    // One step of NOAA's geomagnetic storm scale, e.g. Kp 5 = G1 minor.
    public sealed record StormLevel(int Kp, string GScale, string Name);

    // A plain-language explanation of a space-weather term.
    // Scale is only set for Kp, which shows NOAA's storm scale as a small legend.
    public sealed record GlossaryEntry(string Id, string Term, string Text, IReadOnlyList<StormLevel>? Scale = null);

    // The words behind the ⓘ buttons on the Space Weather card. They live here, not in the views,
    // so the site and a future app endpoint (/api/glossary) explain things the same way.
    public static class Glossary
    {
        public const string LearnMoreUrl = "https://www.swpc.noaa.gov/";

        public static readonly IReadOnlyList<StormLevel> StormScale =
        [
            new(5, "G1", "minor"),
            new(6, "G2", "moderate"),
            new(7, "G3", "strong"),
            new(8, "G4", "severe"),
            new(9, "G5", "extreme"),
        ];

        public static readonly GlossaryEntry Kp = new("kp", "Kp Index",
            "A 0–9 score for how stirred up Earth’s magnetic field is, measured every 3 hours by magnetometers " +
            "around the world. 0–2 is quiet, 3–4 is unsettled, and 5 or more is a geomagnetic storm. The higher " +
            "it goes, the farther from the poles the northern lights reach. Around Kp 5, people in Washington " +
            "State can sometimes see them low in the north.",
            StormScale);

        // Matches FetchDailyDigest.AuroraFromKp. If that mapping changes, change this text.
        public static readonly GlossaryEntry Aurora = new("aurora", "Aurora Chance",
            "A rough guide based only on the current Kp: Unlikely below 4, Possible at 4–5, Likely at 6–7, " +
            "Strong at 8+. It isn’t for your exact location, so use “Your sky this week” for odds where you are. " +
            "Clouds and city lights matter too.");

        public static readonly GlossaryEntry SolarWind = new("solar-wind", "Solar Wind",
            "A stream of charged particles flowing from the Sun, measured by spacecraft about 1.5 million km " +
            "(1 million miles) toward the Sun from Earth. It usually blows at 300–500 km/s. Faster than about " +
            "600 km/s often means stormier space weather, and whatever is measured now reaches Earth roughly " +
            "30–60 minutes later.");

        public static readonly GlossaryEntry KpForecast = new("kp-forecast", "3-Day Kp Forecast",
            "NOAA’s forecast of the highest Kp expected each day. It’s a forecast, so big storms can arrive " +
            "earlier or later than predicted.");

        public static readonly IReadOnlyList<GlossaryEntry> All = [Kp, Aurora, SolarWind, KpForecast];
    }
}
