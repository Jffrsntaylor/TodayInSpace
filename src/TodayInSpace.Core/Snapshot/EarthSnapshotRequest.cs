using System.Globalization;
using TodayInSpace.Core.Visibility;

namespace TodayInSpace.Core.Snapshot
{
    public enum SnapshotFormat
    {
        Square, // the globe alone on a transparent square, for widgets
        Card,   // 1200×630 on the site's dark background, for link previews
    }

    // The checked, coarsened inputs to /api/sky/earth.png.
    // Lat/Lon are already rounded to SkyLocation's 0.05° grid and are null when no place was given.
    public sealed record EarthSnapshotRequest(double? Lat, double? Lon, bool CenterOnIss, int Size, SnapshotFormat Format)
    {
        public const int MinSize = 200;
        public const int MaxSize = 800;
        public const int DefaultSize = 400;
        public const int CardWidth = 1200;
        public const int CardHeight = 630;

        // Widgets refresh every 15-30 minutes at best, and the ISS moves about 4,500 km in 10. So one picture
        // per place per 10 minutes is plenty, and a crowd of widget refreshes costs one render.
        public static readonly TimeSpan Bucket = TimeSpan.FromMinutes(10);

        public bool HasPin => Lat != null;
        public int Width => Format == SnapshotFormat.Card ? CardWidth : Size;
        public int Height => Format == SnapshotFormat.Card ? CardHeight : Size;

        // Null means a bad request. Lat and lon go together: one without the other is an error, as is
        // anything SkyLocation won't accept. An out-of-range size is clamped rather than refused, since a
        // widget asking for 1000 px still wants a picture. The card has a fixed size, so size is ignored.
        public static EarthSnapshotRequest? Parse(double? lat, double? lon, string? center, int? size, string? format)
        {
            if ((lat == null) != (lon == null))
                return null;
            if (lat != null && !SkyLocation.IsValid(lat, lon))
                return null;

            bool centerOnIss;
            if (string.IsNullOrEmpty(center))
                centerOnIss = lat == null;
            else if (string.Equals(center, "iss", StringComparison.OrdinalIgnoreCase))
                centerOnIss = true;
            else
                return null;

            SnapshotFormat fmt;
            if (string.IsNullOrEmpty(format) || string.Equals(format, "square", StringComparison.OrdinalIgnoreCase))
                fmt = SnapshotFormat.Square;
            else if (string.Equals(format, "card", StringComparison.OrdinalIgnoreCase))
                fmt = SnapshotFormat.Card;
            else
                return null;

            int px = fmt == SnapshotFormat.Card ? CardHeight : Math.Clamp(size ?? DefaultSize, MinSize, MaxSize);

            return new EarthSnapshotRequest(
                lat == null ? null : SkyLocation.Round(lat.Value),
                lon == null ? null : SkyLocation.Round(lon.Value),
                centerOnIss, px, fmt);
        }

        public static DateTime BucketStart(DateTime utc) =>
            new(utc.Ticks - utc.Ticks % Bucket.Ticks, DateTimeKind.Utc);

        // Contains the (rounded) location, so like SkyLocation's key it must never be logged.
        public string CacheKey(DateTime utc)
        {
            string place = Lat == null ? "none" : string.Create(CultureInfo.InvariantCulture, $"{Lat:F2}:{Lon:F2}");
            return string.Create(CultureInfo.InvariantCulture,
                $"sky:earth:{place}:{(CenterOnIss ? "iss" : "place")}:{Format}:{Size}:{BucketStart(utc):yyyyMMddHHmm}");
        }
    }
}
