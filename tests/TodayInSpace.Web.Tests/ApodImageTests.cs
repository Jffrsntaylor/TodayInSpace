using TodayInSpace.Core;
using TodayInSpace.Web.Helpers;
using TodayInSpace.Web.Models;

namespace TodayInSpace.Web.Tests
{
    public class ApodImageNamingTests
    {
        [Theory]
        [InlineData("https://apod.nasa.gov/apod/image/2609/CosmicLatte.jpg", "2026-09-28.jpg")]
        [InlineData("https://apod.nasa.gov/apod/image/2609/Nebula.PNG", "2026-09-28.png")]
        [InlineData("https://apod.nasa.gov/apod/image/2609/anim.gif", "2026-09-28.gif")]
        [InlineData("https://science.nasa.gov/apod/image/2609/x.jpeg?size=large", "2026-09-28.jpeg")]
        public void GetBlobName_UsesDateAndSourceExtension(string url, string expected)
        {
            Assert.Equal(expected, ApodImageNaming.GetBlobName("2026-09-28", url));
        }

        [Fact]
        public void GetBlobName_DefaultsToJpg_ForUnknownExtension()
        {
            Assert.Equal("2026-09-28.jpg",
                ApodImageNaming.GetBlobName("2026-09-28", "https://example.com/image?id=42"));
        }

        [Theory]
        [InlineData(null, "https://apod.nasa.gov/a.jpg")]      // no date
        [InlineData("09/28/2026", "https://apod.nasa.gov/a.jpg")] // wrong date format
        [InlineData("2026-09-28", null)]                        // no URL (video day)
        [InlineData("2026-09-28", "")]
        [InlineData("2026-09-28", "not a url")]
        [InlineData("2026-09-28", "ftp://apod.nasa.gov/a.jpg")] // not http(s)
        public void GetBlobName_ReturnsNull_ForUnusableInput(string? date, string? url)
        {
            Assert.Null(ApodImageNaming.GetBlobName(date, url));
        }

        [Theory]
        [InlineData("2026-09-28.jpg", true)]
        [InlineData("2026-09-28.webp", true)]
        [InlineData("latest.json", false)]           // digest files aren't images
        [InlineData("2026-09-28.json", false)]
        [InlineData("../digests/latest.json", false)] // path traversal
        [InlineData("2026-09-28.jpg/../x", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsValidBlobName_OnlyAcceptsDatedImages(string? name, bool expected)
        {
            Assert.Equal(expected, ApodImageNaming.IsValidBlobName(name));
        }

        [Theory]
        [InlineData("2026-09-28.jpg", "image/jpeg")]
        [InlineData("2026-09-28.jpeg", "image/jpeg")]
        [InlineData("2026-09-28.png", "image/png")]
        [InlineData("2026-09-28.gif", "image/gif")]
        [InlineData("2026-09-28.bin", "application/octet-stream")]
        public void GetContentType_MapsExtension(string name, string expected)
        {
            Assert.Equal(expected, ApodImageNaming.GetContentType(name));
        }
    }

    public class ApodImageHelperTests
    {
        [Fact]
        public void GetDisplayUrl_PrefersArchivedCopy()
        {
            var apod = new ApodInfo { ImageUrl = "https://apod.nasa.gov/a.jpg", ImageBlob = "2026-09-28.jpg" };
            Assert.Equal("/images/2026-09-28.jpg", ApodImageHelper.GetDisplayUrl(apod));
        }

        [Fact]
        public void GetDisplayUrl_FallsBackToNasa_WhenNotArchived()
        {
            var apod = new ApodInfo { ImageUrl = "https://apod.nasa.gov/a.jpg" };
            Assert.Equal("https://apod.nasa.gov/a.jpg", ApodImageHelper.GetDisplayUrl(apod));
        }

        [Fact]
        public void GetDisplayUrl_IgnoresInvalidBlobName()
        {
            var apod = new ApodInfo { ImageUrl = "https://apod.nasa.gov/a.jpg", ImageBlob = "../secret.json" };
            Assert.Equal("https://apod.nasa.gov/a.jpg", ApodImageHelper.GetDisplayUrl(apod));
        }

        [Fact]
        public void GetDisplayUrl_ReturnsNull_WhenNoImage()
        {
            Assert.Null(ApodImageHelper.GetDisplayUrl(new ApodInfo { ImageUrl = "" }));
            Assert.Null(ApodImageHelper.GetDisplayUrl(null));
        }
    }
}
