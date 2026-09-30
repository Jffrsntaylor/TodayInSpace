using TodayInSpace.Core;
using TodayInSpace.Web.Helpers;
using TodayInSpace.Web.Models;

namespace TodayInSpace.Web.Tests
{
    public class ApodVideoTests
    {
        [Theory]
        [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ?rel=0", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?rel=0")]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=3", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?rel=0")]
        [InlineData("https://youtu.be/1wqd_RSEhHo", "https://www.youtube-nocookie.com/embed/1wqd_RSEhHo?rel=0")]
        [InlineData("https://player.vimeo.com/video/123456789", "https://player.vimeo.com/video/123456789")]
        [InlineData("https://vimeo.com/123456789", "https://player.vimeo.com/video/123456789")]
        public void KnownPlayers_BecomeIframes(string url, string expected)
        {
            var embed = ApodVideo.ToEmbed(url);
            Assert.NotNull(embed);
            Assert.Equal(VideoKind.Iframe, embed!.Kind);
            Assert.Equal(expected, embed.Src);
        }

        [Theory]
        [InlineData("https://assets.science.nasa.gov/content/dam/science/cds/apod/apod/2026/august/eso2612b.mp4")]
        [InlineData("https://apod.nasa.gov/apod/image/2401/clip.webm")]
        public void NasaVideoFiles_BecomeVideoTags(string url)
        {
            var embed = ApodVideo.ToEmbed(url);
            Assert.NotNull(embed);
            Assert.Equal(VideoKind.File, embed!.Kind);
            Assert.StartsWith("https://", embed.Src);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a url")]
        [InlineData("javascript:alert(1)")]
        [InlineData("https://evil.example.com/embed/abc")]          // unknown host
        [InlineData("https://evil.example.com/video.mp4")]          // files only from nasa.gov
        [InlineData("https://nasa.gov.evil.com/video.mp4")]         // look-alike host
        [InlineData("https://www.youtube.com/watch?v=bad id!")]     // malformed id
        [InlineData("https://assets.science.nasa.gov/image.jpg")]   // not a video file
        public void AnythingElse_IsNotEmbedded(string? url)
        {
            Assert.Null(ApodVideo.ToEmbed(url));
        }

        [Fact]
        public void ArticleMedia_ReadsVideoAndSnapshot()
        {
            const string html = @"<head>
<meta property=""og:image"" content=""https://assets.science.nasa.gov/content/dam/science/cds/apod/apod/2026/august/eso2612b_snapshot.jpeg/jcr:content/renditions/cq5dam.web.1280.1280.jpeg"">
<meta property=""og:video"" content=""https://assets.science.nasa.gov/content/dam/science/cds/apod/apod/2026/august/eso2612b.mp4"">
</head>";
            var media = ApodSources.ParseArticleMedia(html);
            Assert.Equal("https://assets.science.nasa.gov/content/dam/science/cds/apod/apod/2026/august/eso2612b.mp4", media.VideoUrl);
            Assert.EndsWith("cq5dam.web.1280.1280.jpeg", media.ImageUrl);
        }

        [Fact]
        public void ArticleMedia_PictureDayHasNoVideo()
        {
            var media = ApodSources.ParseArticleMedia(@"<meta content=""https://x.nasa.gov/a.jpg"" property=""og:image"" />");
            Assert.Equal("", media.VideoUrl);
            Assert.Equal("https://x.nasa.gov/a.jpg", media.ImageUrl);   // content-first attribute order
        }

        [Fact]
        public void GlowUsesThumbnail_OnVideoDays()
        {
            var apod = new ApodInfo { Title = "t", VideoUrl = "https://youtu.be/1wqd_RSEhHo", ThumbnailUrl = "https://img.youtube.com/vi/1wqd_RSEhHo/0.jpg" };
            Assert.Equal("https://img.youtube.com/vi/1wqd_RSEhHo/0.jpg", ApodImageHelper.GetGlowUrl(apod));
            Assert.NotNull(ApodImageHelper.GetVideo(apod));
        }

        [Fact]
        public void GlowIgnoresNonHttpsThumbnail()
        {
            Assert.Null(ApodImageHelper.GetGlowUrl(new ApodInfo { ThumbnailUrl = "javascript:alert(1)" }));
        }
    }
}
