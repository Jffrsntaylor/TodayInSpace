namespace TodayInSpace.Core.Snapshot
{
    // One point of the ISS's ground track. Minutes is relative to now: negative behind, positive ahead.
    public record TrackPoint(GroundPoint Point, double Minutes);

    // Everything the Earth snapshot shows, worked out before any pixels are drawn.
    // Aurora is OVATION's [lon, lat, probability %] points; empty when the forecast isn't available.
    public sealed record EarthScene(
        GroundPoint Center,
        GroundPoint Subsolar,
        GroundPoint? Iss,
        IReadOnlyList<TrackPoint> Track,
        GroundPoint? Pin,
        bool IssAbovePin,
        IReadOnlyList<int[]> Aurora)
    {
        // The same reach as the globe's ISS path: where it's just been and where it's going next.
        public static readonly TimeSpan TrackBehind = TimeSpan.FromMinutes(20);
        public static readonly TimeSpan TrackAhead = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan TrackStep = TimeSpan.FromSeconds(30);

        // The pin gets a ring when the ISS is at least this high over it, the same 10° the pass list uses.
        public const double RingElevation = 10;

        // Null when the picture is meant to be centred on the ISS but there's no orbit data for it.
        // Without orbit data a place-centred picture still works, just without the ISS.
        public static EarthScene? Build(EarthSnapshotRequest request, DateTime nowUtc, OrbitTrack? iss, IReadOnlyList<int[]>? aurora)
        {
            if (request.CenterOnIss && iss == null)
                return null;

            GroundPoint? issNow = iss?.SubPoint(nowUtc);
            var track = new List<TrackPoint>();
            if (iss != null)
            {
                for (var t = nowUtc - TrackBehind; t <= nowUtc + TrackAhead; t += TrackStep)
                    track.Add(new TrackPoint(iss.SubPoint(t), (t - nowUtc).TotalMinutes));
            }

            GroundPoint? pin = request.HasPin ? new GroundPoint(request.Lat!.Value, request.Lon!.Value) : null;
            bool above = pin != null && iss != null && iss.ElevationFrom(pin.Lat, pin.Lon, nowUtc) >= RingElevation;

            var center = request.CenterOnIss ? issNow! : pin!;

            return new EarthScene(center, DayNight.SubsolarPoint(nowUtc), issNow, track, pin, above,
                aurora ?? Array.Empty<int[]>());
        }
    }
}
