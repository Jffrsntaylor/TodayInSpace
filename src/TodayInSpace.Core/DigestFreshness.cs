using System.Globalization;

namespace TodayInSpace.Core
{
    public enum FreshnessLevel
    {
        Fresh,
        Stale,      // a scheduled run was missed
        Missing     // a whole day of runs was missed, or the date can't be read
    }

    // How old the latest digest is, for the health check.
    public static class DigestFreshness
    {
        // The function runs at 10:00 and 18:00 UTC. Age is counted from midnight UTC of the digest's
        // date, so a normal digest is never older than ~34h. 36h means the next morning's run is
        // two hours late; 60h means a whole day of runs failed.
        public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(36);
        public static readonly TimeSpan MissingAfter = TimeSpan.FromHours(60);

        // "now" is passed in rather than read from the clock so tests can pin it.
        // Age is null when the date is missing or isn't "yyyy-MM-dd".
        public static (FreshnessLevel Level, TimeSpan? Age) Evaluate(string? digestDate, DateTimeOffset nowUtc)
        {
            if (!DateOnly.TryParseExact(digestDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return (FreshnessLevel.Missing, null);

            var startOfDay = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var age = nowUtc - startOfDay;

            if (age >= MissingAfter) return (FreshnessLevel.Missing, age);
            if (age >= StaleAfter) return (FreshnessLevel.Stale, age);
            return (FreshnessLevel.Fresh, age);
        }
    }
}
