using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    public class DigestFreshnessTests
    {
        // Ages are measured from midnight UTC of the digest's date.
        private const string Date = "2026-10-01";
        private static readonly DateTimeOffset Midnight = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Evaluate_DigestWrittenThisMorning_IsFresh()
        {
            var (level, age) = DigestFreshness.Evaluate(Date, Midnight.AddHours(10).AddMinutes(5));

            Assert.Equal(FreshnessLevel.Fresh, level);
            Assert.Equal(TimeSpan.FromMinutes(605), age);
        }

        [Theory]
        [InlineData(35, FreshnessLevel.Fresh)]
        [InlineData(36, FreshnessLevel.Stale)]    // boundary: stale from 36h on
        [InlineData(37, FreshnessLevel.Stale)]
        [InlineData(59, FreshnessLevel.Stale)]
        [InlineData(60, FreshnessLevel.Missing)]  // boundary: missing from 60h on
        [InlineData(61, FreshnessLevel.Missing)]
        public void Evaluate_ByAgeInHours(int hours, FreshnessLevel expected)
        {
            var (level, age) = DigestFreshness.Evaluate(Date, Midnight.AddHours(hours));

            Assert.Equal(expected, level);
            Assert.Equal(TimeSpan.FromHours(hours), age);
        }

        [Fact]
        public void Evaluate_DateAheadOfClock_IsFresh()
        {
            // A little clock skew between the function and the web app shouldn't page anyone.
            Assert.Equal(FreshnessLevel.Fresh, DigestFreshness.Evaluate(Date, Midnight.AddMinutes(-5)).Level);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Evaluate_MissingDate_IsMissing(string? date)
        {
            Assert.Equal((FreshnessLevel.Missing, (TimeSpan?)null), DigestFreshness.Evaluate(date, Midnight));
        }

        [Theory]
        [InlineData("not a date")]
        [InlineData("10/01/2026")]
        [InlineData("2026-13-01")]
        [InlineData("2026-10-01T00:00:00")]
        public void Evaluate_UnparseableDate_IsMissing(string date)
        {
            Assert.Equal((FreshnessLevel.Missing, (TimeSpan?)null), DigestFreshness.Evaluate(date, Midnight));
        }
    }
}
