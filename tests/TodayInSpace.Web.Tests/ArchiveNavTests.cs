using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    public class ArchiveNavTests
    {
        private static readonly DateOnly Today = new(2026, 10, 1);

        [Fact]
        public void GetNeighbors_NormalDay_ReturnsDayBeforeAndAfter()
        {
            Assert.Equal(("2026-09-14", "2026-09-16"), ArchiveNav.GetNeighbors("2026-09-15", Today));
        }

        [Theory]
        [InlineData("2026-03-01", "2026-02-28", "2026-03-02")] // month boundary, non-leap year
        [InlineData("2026-09-30", "2026-09-29", "2026-10-01")] // next lands on today
        [InlineData("2026-01-01", "2025-12-31", "2026-01-02")] // year boundary going back
        [InlineData("2025-12-31", "2025-12-30", "2026-01-01")] // year boundary going forward
        public void GetNeighbors_CrossesMonthAndYearBoundaries(string date, string prev, string next)
        {
            Assert.Equal((prev, next), ArchiveNav.GetNeighbors(date, Today));
        }

        [Theory]
        [InlineData("2024-03-01", "2024-02-29", "2024-03-02")] // back onto a leap day
        [InlineData("2024-02-28", "2024-02-27", "2024-02-29")] // forward onto a leap day
        [InlineData("2024-02-29", "2024-02-28", "2024-03-01")] // the leap day itself
        public void GetNeighbors_HandlesLeapDay(string date, string prev, string next)
        {
            Assert.Equal((prev, next), ArchiveNav.GetNeighbors(date, Today));
        }

        [Fact]
        public void GetNeighbors_Today_HasNoNext()
        {
            Assert.Equal(("2026-09-30", (string?)null), ArchiveNav.GetNeighbors("2026-10-01", Today));
        }

        [Fact]
        public void GetNeighbors_FutureDate_HasNoNext()
        {
            Assert.Equal(("2026-12-24", (string?)null), ArchiveNav.GetNeighbors("2026-12-25", Today));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("09/28/2026")]   // wrong format
        [InlineData("2026-9-28")]    // missing zero padding
        [InlineData("2026-13-01")]   // no 13th month
        [InlineData("2026-02-30")]   // no Feb 30
        [InlineData("2026-09-28T00:00:00")]
        [InlineData("not a date")]
        public void GetNeighbors_InvalidInput_ReturnsNoLinks(string? date)
        {
            Assert.Equal(((string?)null, (string?)null), ArchiveNav.GetNeighbors(date, Today));
        }

        [Theory]
        [InlineData(2026, 10, 1, "2026-09-30")] // normal day
        [InlineData(2026, 1, 1, "2025-12-31")]  // back into the previous year
        [InlineData(2024, 3, 1, "2024-02-29")]  // back onto a leap day
        public void DefaultDate_IsYesterday(int year, int month, int day, string expected)
        {
            Assert.Equal(expected, ArchiveNav.DefaultDate(new DateOnly(year, month, day)));
        }
    }
}
