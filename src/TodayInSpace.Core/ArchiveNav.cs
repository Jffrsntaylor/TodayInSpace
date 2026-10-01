using System.Globalization;

namespace TodayInSpace.Core
{
    // Previous/next day links for the archive page.
    public static class ArchiveNav
    {
        private const string DateFormat = "yyyy-MM-dd";

        // Returns the days either side of a "yyyy-MM-dd" date, or (null, null) if it doesn't parse.
        // "Today" is passed in rather than read from the clock so tests can pin it.
        // Next is null on today or later, because no digest exists for the future
        // and the date picker already caps at today.
        public static (string? Prev, string? Next) GetNeighbors(string? date, DateOnly todayUtc)
        {
            if (!DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return (null, null);

            string prev = day.AddDays(-1).ToString(DateFormat, CultureInfo.InvariantCulture);
            string? next = day < todayUtc
                ? day.AddDays(1).ToString(DateFormat, CultureInfo.InvariantCulture)
                : null;

            return (prev, next);
        }
    }
}
