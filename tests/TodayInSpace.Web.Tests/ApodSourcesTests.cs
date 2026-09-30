using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    public class ApodSourcesTests
    {
        // ---------- Sanity check on API answers ----------

        [Fact]
        public void RealEntry_IsAccepted()
        {
            Assert.True(ApodSources.LooksLikeRealApod(
                "Arp 78: Peculiar Galaxy in Aries", "Peculiar spiral galaxy Arp 78...",
                "https://assets.science.nasa.gov/dynamicimage/assets/science/cds/apod/apod/2026/october/NGC772.jpg"));
            Assert.True(ApodSources.LooksLikeRealApod(
                "Old style", "text", "https://apod.nasa.gov/apod/image/2609/Shrimp.jpg"));
        }

        [Fact]
        public void VideoDay_WithNoImage_IsAccepted()
        {
            Assert.True(ApodSources.LooksLikeRealApod("A Video", "text", ""));
        }

        [Theory]
        [InlineData("NASA Science")]          // what the API returned on 2026-09-30
        [InlineData("APOD - NASA Science")]
        [InlineData("")]
        [InlineData(null)]
        public void GenericOrMissingTitle_IsRejected(string? title)
        {
            Assert.False(ApodSources.LooksLikeRealApod(title, "text", "https://apod.nasa.gov/apod/image/a.jpg"));
        }

        [Theory]
        [InlineData("https://www.nasa.gov/wp-content/themes/nasa/assets/images/nasa-logo.svg")]
        [InlineData("https://science.nasa.gov/wp-content/uploads/nasa-meatball.png")]  // no "apod" in path
        [InlineData("not a url")]
        public void NonApodImage_IsRejected(string url)
        {
            Assert.False(ApodSources.LooksLikeRealApod("Arp 78", "text", url));
        }

        [Fact]
        public void MissingExplanation_IsRejected()
        {
            Assert.False(ApodSources.LooksLikeRealApod("Arp 78", " ", "https://apod.nasa.gov/apod/image/a.jpg"));
        }

        // ---------- RSS backup ----------

        private const string Feed = @"<?xml version=""1.0""?>
<rss version=""2.0"" xmlns:apod=""https://science.nasa.gov/apod-ns"">
<channel><title>APOD Basic</title>
<item>
  <title>Sh2-188: The Shrimp Nebula</title>
  <link>https://science.nasa.gov/image-article/apod-2026-september-29-sh2-188-the-shrimp-nebula/</link>
  <pubDate>Tue, 29 Sep 2026 04:05:00 +0000</pubDate>
  <apod:hdurl>https://assets.science.nasa.gov/dynamicimage/assets/science/cds/apod/apod/2026/september/Shrimp_Pawel_2048.jpg?w=2048&amp;h=2560&amp;fit=clip</apod:hdurl>
  <apod:copyright><![CDATA[<a href=""https://example.com"">Pawel Piechnik</a>]]></apod:copyright>
  <apod:explanation><![CDATA[<p>What causes the swirl in the <a href=""x"">Shrimp Nebula</a>?</p>  Its high speed&nbsp;is likely.]]></apod:explanation>
</item>
<item>
  <title>Cosmic Latte</title>
  <link>https://science.nasa.gov/image-article/apod-2026-september-28-cosmic-latte/</link>
  <apod:hdurl>https://assets.science.nasa.gov/dynamicimage/assets/science/cds/apod/apod/2026/september/CosmicLatte.jpg?w=960</apod:hdurl>
  <apod:explanation>What color is the universe?</apod:explanation>
</item>
</channel></rss>";

        [Fact]
        public void Rss_FindsTheRequestedDay()
        {
            var entry = ApodSources.ParseRssForDate(Feed, new DateOnly(2026, 9, 29));

            Assert.NotNull(entry);
            Assert.Equal("Sh2-188: The Shrimp Nebula", entry!.Title);
            Assert.Equal("What causes the swirl in the Shrimp Nebula? Its high speed is likely.", entry.Explanation);
            Assert.Equal("Pawel Piechnik", entry.Copyright);
            Assert.Equal("2026-09-29", entry.Date);
            Assert.Equal("https://assets.science.nasa.gov/dynamicimage/assets/science/cds/apod/apod/2026/september/Shrimp_Pawel_2048.jpg?w=1600&h=1600&fit=clip", entry.ImageUrl);
        }

        [Fact]
        public void Rss_ReturnsNull_WhenDayIsNotInFeedYet()
        {
            Assert.Null(ApodSources.ParseRssForDate(Feed, new DateOnly(2026, 9, 30)));
        }

        [Fact]
        public void Rss_DoesNotConfuseSimilarDates()
        {
            // "september-2-" must not match "september-29-" / "september-28-"
            Assert.Null(ApodSources.ParseRssForDate(Feed, new DateOnly(2026, 9, 2)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<html>not a feed")]
        public void Rss_ReturnsNull_ForBadInput(string? xml)
        {
            Assert.Null(ApodSources.ParseRssForDate(xml, new DateOnly(2026, 9, 29)));
        }

        [Fact]
        public void SizeForWeb_LeavesOtherHostsAlone()
        {
            Assert.Equal("https://apod.nasa.gov/apod/image/a.jpg", ApodSources.SizeForWeb("https://apod.nasa.gov/apod/image/a.jpg"));
        }
    }
}
