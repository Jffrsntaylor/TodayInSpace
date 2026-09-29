using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    public class NoaaParsersTests
    {
        [Theory]
        [InlineData(@"[{""proton_speed"": 280, ""time_tag"": ""2026-09-29T22:10:00Z""}]", 280)]   // current NOAA format
        [InlineData(@"[{""proton_speed"": 412.6}]", 413)]                                          // rounds
        [InlineData(@"[{""proton_speed"": 300}, {""proton_speed"": 350}]", 350)]                   // takes the latest
        [InlineData(@"{""WindSpeed"": ""455"", ""TimeStamp"": ""x""}", 455)]                      // older format, string number
        public void ParseSolarWindSpeed_ReadsSpeed(string json, int expected)
        {
            Assert.Equal(expected, NoaaParsers.ParseSolarWindSpeed(json));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData(@"[{""proton_speed"": null}]")]
        [InlineData(@"[{""proton_speed"": -1}]")]
        [InlineData(@"[{""time_tag"": ""2026-09-29T22:10:00Z""}]")]
        [InlineData("<html>Not Found</html>")]                          // what the retired endpoint returns
        public void ParseSolarWindSpeed_ReturnsNull_WhenNoUsableValue(string? json)
        {
            Assert.Null(NoaaParsers.ParseSolarWindSpeed(json));
        }
    }
}
