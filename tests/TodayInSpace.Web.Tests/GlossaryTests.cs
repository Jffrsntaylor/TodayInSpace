using TodayInSpace.Core;
using Xunit;

namespace TodayInSpace.Web.Tests
{
    public class GlossaryTests
    {
        [Fact]
        public void EveryEntry_HasAUniqueIdAndSomeText()
        {
            Assert.Equal(5, Glossary.All.Count);
            Assert.Equal(Glossary.All.Count, Glossary.All.Select(e => e.Id).Distinct().Count());
            Assert.All(Glossary.All, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Term));
                Assert.False(string.IsNullOrWhiteSpace(e.Text));
            });
        }

        [Fact]
        public void StormScale_FollowsNoaasGScale()
        {
            Assert.Equal(
                ["5 G1 minor", "6 G2 moderate", "7 G3 strong", "8 G4 severe", "9 G5 extreme"],
                Glossary.StormScale.Select(l => $"{l.Kp} {l.GScale} {l.Name}"));
        }

        [Fact]
        public void OnlyKp_ShowsTheStormScale()
        {
            Assert.Same(Glossary.StormScale, Glossary.Kp.Scale);
            Assert.All(Glossary.All.Where(e => e != Glossary.Kp), e => Assert.Null(e.Scale));
        }

        [Fact]
        public void LearnMore_PointsAtNoaaSwpc()
        {
            Assert.Equal("https://www.swpc.noaa.gov/", Glossary.LearnMoreUrl);
            Assert.All(Glossary.All.Where(e => e != Glossary.Sgp4), e =>
            {
                Assert.Equal(Glossary.LearnMoreUrl, e.LearnMoreUrl);
                Assert.Equal("NOAA SWPC", e.LearnMoreSource);
            });
        }

        [Fact]
        public void Sgp4_LinksToCelesTrakNotNoaa()
        {
            Assert.Equal("CelesTrak", Glossary.Sgp4.LearnMoreSource);
            Assert.StartsWith("https://celestrak.org/", Glossary.Sgp4.LearnMoreUrl);
        }

        [Fact]
        public void Sgp4_KeepsTheAccuracyClaimModest()
        {
            // Public TLEs with SGP4 are good to kilometers, not meters. Don't let the wording drift into overclaiming.
            Assert.Contains("a few kilometers", Glossary.Sgp4.Text);
            Assert.DoesNotMatch(@"(?i)\bmet(er|re)s?\b", Glossary.Sgp4.Text);
        }
    }
}
