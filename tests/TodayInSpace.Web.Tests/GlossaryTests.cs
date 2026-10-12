using TodayInSpace.Core;
using Xunit;

namespace TodayInSpace.Web.Tests
{
    public class GlossaryTests
    {
        [Fact]
        public void EveryEntry_HasAUniqueIdAndSomeText()
        {
            Assert.Equal(4, Glossary.All.Count);
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
        }
    }
}
