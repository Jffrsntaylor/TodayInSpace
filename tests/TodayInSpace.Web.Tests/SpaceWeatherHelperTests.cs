using Xunit;
using TodayInSpace.Web.Helpers;

namespace TodayInSpace.Web.Tests
{
    public class SpaceWeatherHelperTests
    {
        // ---------- GetKpSeverity ----------

        [Theory]
        [InlineData(0, "calm")]
        [InlineData(3, "calm")]
        [InlineData(4, "watch")]
        [InlineData(5, "watch")]
        [InlineData(6, "alert")]
        [InlineData(9, "alert")]
        public void GetKpSeverity_ReturnsCorrectCategory_ForValidValues(int kp, string expected)
        {
            var result = SpaceWeatherHelper.GetKpSeverity(kp);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GetKpSeverity_ReturnsInfo_WhenNull()
        {
            // Edge case: no data
            var result = SpaceWeatherHelper.GetKpSeverity(null);
            Assert.Equal("info", result);
        }

        [Theory]
        [InlineData(-1)]   // below range
        [InlineData(10)]   // above range ("too much")
        [InlineData(999)]  // way out of range
        public void GetKpSeverity_ReturnsInfo_ForOutOfRangeValues(int kp)
        {
            var result = SpaceWeatherHelper.GetKpSeverity(kp);
            Assert.Equal("info", result);
        }

        // ---------- GetKpDescription ----------

        [Theory]
        [InlineData(0, "quiet")]
        [InlineData(2, "quiet")]
        [InlineData(4, "unsettled")]
        [InlineData(6, "minor storm")]
        [InlineData(8, "severe storm")]
        public void GetKpDescription_ReturnsExpectedText(int kp, string expected)
        {
            Assert.Equal(expected, SpaceWeatherHelper.GetKpDescription(kp));
        }

        [Fact]
        public void GetKpDescription_ReturnsUnavailable_WhenNull()
        {
            Assert.Equal("unavailable", SpaceWeatherHelper.GetKpDescription(null));
        }

        // ---------- GetAuroraSeverity ----------

        [Theory]
        [InlineData("UNLIKELY", "info")]
        [InlineData("POSSIBLE", "watch")]
        [InlineData("LIKELY", "alert")]
        [InlineData("STRONG", "alert")]
        [InlineData("UNAVAILABLE", "info")]
        public void GetAuroraSeverity_ReturnsCorrectCategory(string chance, string expected)
        {
            Assert.Equal(expected, SpaceWeatherHelper.GetAuroraSeverity(chance));
        }

        [Theory]
        [InlineData("possible", "watch")]   // lowercase
        [InlineData("  POSSIBLE  ", "watch")] // extra spaces
        public void GetAuroraSeverity_IsCaseAndWhitespaceInsensitive(string chance, string expected)
        {
            Assert.Equal(expected, SpaceWeatherHelper.GetAuroraSeverity(chance));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("banana")]   // unknown/garbage label
        public void GetAuroraSeverity_ReturnsInfo_ForMissingOrUnknown(string? chance)
        {
            Assert.Equal("info", SpaceWeatherHelper.GetAuroraSeverity(chance));
        }

        // ---------- GetForecastBarPercent ----------

        [Theory]
        [InlineData(9, 100)]   // max
        [InlineData(0, 8)]     // min clamp (Kp 0 still shows a sliver)
        [InlineData(5, 55)]    // mid-range (5/9 = 55%)
        public void GetForecastBarPercent_ScalesCorrectly(int kp, int expected)
        {
            Assert.Equal(expected, SpaceWeatherHelper.GetForecastBarPercent(kp));
        }

        [Fact]
        public void GetForecastBarPercent_ReturnsMinimum_WhenNull()
        {
            Assert.Equal(8, SpaceWeatherHelper.GetForecastBarPercent(null));
        }

        [Fact]
        public void GetForecastBarPercent_NeverExceeds100()
        {
            // Edge case: "too much" data
            Assert.Equal(100, SpaceWeatherHelper.GetForecastBarPercent(50));
        }
    }
}
