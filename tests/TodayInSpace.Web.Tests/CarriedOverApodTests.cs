using TodayInSpace.Web.Helpers;
using TodayInSpace.Web.Models;

namespace TodayInSpace.Web.Tests
{
    public class CarriedOverApodTests
    {
        private static DigestModel Digest(string date, string? apodDate) =>
            new DigestModel { Date = date, Apod = new ApodInfo { Title = "x", Date = apodDate } };

        [Fact]
        public void ReturnsNull_WhenPictureIsFromTheSameDay()
        {
            Assert.Null(ApodImageHelper.GetCarriedOverDate(Digest("2026-09-30", "2026-09-30")));
        }

        [Fact]
        public void ReturnsOriginalDate_WhenPictureWasCarriedOver()
        {
            Assert.Equal(new DateTime(2026, 9, 29), ApodImageHelper.GetCarriedOverDate(Digest("2026-09-30", "2026-09-29")));
        }

        [Theory]
        [InlineData(null)]          // older digests without a picture date
        [InlineData("")]
        [InlineData("not a date")]
        public void ReturnsNull_WhenPictureDateIsMissingOrInvalid(string? apodDate)
        {
            Assert.Null(ApodImageHelper.GetCarriedOverDate(Digest("2026-09-30", apodDate)));
        }

        [Fact]
        public void ReturnsNull_WhenThereIsNoPicture()
        {
            Assert.Null(ApodImageHelper.GetCarriedOverDate(new DigestModel { Date = "2026-09-30" }));
            Assert.Null(ApodImageHelper.GetCarriedOverDate(null));
        }
    }
}
