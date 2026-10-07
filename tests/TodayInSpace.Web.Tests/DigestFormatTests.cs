using System.Text.Json;
using TodayInSpace.Core;
using Fn = TodayInSpace.Function;
using WebModels = TodayInSpace.Web.Models;

namespace TodayInSpace.Web.Tests
{
    // The digest JSON in storage is the archive, so its key names must never change by accident.
    // These pin the keys the function writes, and check the web app can read them back.
    public class DigestFormatTests
    {
        private static readonly DateTimeOffset Updated = new(2026, 10, 7, 9, 15, 0, TimeSpan.Zero);

        // Every field set, including the video ones and the nulls a real digest can have.
        private static Fn.DigestModel FullDigest(DigestPeople? people) => new()
        {
            date = "2026-10-07",
            apod = new Fn.ApodInfo
            {
                title = "A Galaxy",
                explanation = "Explanation.",
                imageUrl = "https://apod.nasa.gov/apod/image/2610/galaxy.jpg",
                copyright = "Someone",
                date = "2026-10-07",
                imageBlob = null,
                sourceImageUrl = "https://apod.nasa.gov/apod/image/2610/galaxy.jpg",
                videoUrl = "https://www.youtube.com/embed/abc",
                thumbnailUrl = "https://img.youtube.com/vi/abc/0.jpg"
            },
            spaceWeather = new Fn.SpaceWeatherInfo
            {
                currentKp = 3,
                auroraChance = "UNLIKELY",
                solarWindSpeed = null,
                forecast = { new Fn.ForecastDay { day = "Wed", kp = 2 }, new Fn.ForecastDay { day = "Thu", kp = null } }
            },
            people = people
        };

        private static DigestPeople People() => new(2, Updated, new[]
        {
            new PersonDto("Jessica Meir", "NASA", new[] { new FlagDto("US", "United States of America") }, "ISS"),
            new PersonDto("Wang Yaping", null, new[] { new FlagDto("CN", "China") }, "Tiangong"),
        });

        private static IEnumerable<string> Keys(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name);

        [Fact]
        public void Serialize_WithoutPeople_KeepsTodaysKeyNames()
        {
            using var doc = JsonDocument.Parse(Fn.FetchDailyDigest.Serialize(FullDigest(null)));
            var root = doc.RootElement;

            // The keys in digests written before the people section existed. No "people" key either.
            Assert.Equal(new[] { "date", "apod", "spaceWeather" }, Keys(root));
            Assert.Equal(new[] { "title", "explanation", "imageUrl", "copyright", "date", "imageBlob",
                                 "sourceImageUrl", "videoUrl", "thumbnailUrl" }, Keys(root.GetProperty("apod")));
            Assert.Equal(new[] { "currentKp", "auroraChance", "solarWindSpeed", "forecast" }, Keys(root.GetProperty("spaceWeather")));
            Assert.Equal(new[] { "day", "kp" }, Keys(root.GetProperty("spaceWeather").GetProperty("forecast")[0]));

            // Nulls are still written, as before.
            Assert.Equal(JsonValueKind.Null, root.GetProperty("apod").GetProperty("imageBlob").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("spaceWeather").GetProperty("solarWindSpeed").ValueKind);
        }

        [Fact]
        public void Serialize_WithPeople_AddsCamelCasePeopleSection()
        {
            using var doc = JsonDocument.Parse(Fn.FetchDailyDigest.Serialize(FullDigest(People())));
            var people = doc.RootElement.GetProperty("people");

            Assert.Equal(new[] { "count", "updated", "people" }, Keys(people));
            Assert.Equal(new[] { "name", "agency", "flags", "station" }, Keys(people.GetProperty("people")[0]));
            Assert.Equal(new[] { "code", "country" }, Keys(people.GetProperty("people")[0].GetProperty("flags")[0]));
        }

        // How DigestService reads a digest.
        private static WebModels.DigestModel? Read(string json) =>
            JsonSerializer.Deserialize<WebModels.DigestModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        [Fact]
        public void WebModel_OldDigestWithoutPeople_StillReads()
        {
            var digest = Read(@"{""date"":""2026-09-01"",""apod"":{""title"":""Old Day""},
                ""spaceWeather"":{""currentKp"":2,""auroraChance"":""UNLIKELY"",""solarWindSpeed"":380,""forecast"":[]}}")!;

            Assert.Equal("2026-09-01", digest.Date);
            Assert.Equal("Old Day", digest.Apod!.Title);
            Assert.Null(digest.People);
        }

        [Fact]
        public void WebModel_ReadsPeopleTheFunctionWrote()
        {
            var people = Read(Fn.FetchDailyDigest.Serialize(FullDigest(People())))!.People!;

            Assert.Equal(2, people.Count);
            Assert.Equal(Updated, people.Updated);
            Assert.Equal("Jessica Meir", people.People[0].Name);
            Assert.Equal("ISS", people.People[0].Station);
            Assert.Equal("US", people.People[0].Flags[0].Code);
            Assert.Null(people.People[1].Agency);
            Assert.Equal("Tiangong", people.People[1].Station);
        }

        [Fact]
        public void WebModel_WithPeople_RoundTrips()
        {
            var first = Read(Fn.FetchDailyDigest.Serialize(FullDigest(People())))!;
            var second = Read(JsonSerializer.Serialize(first))!;

            Assert.Equal(first.People!.Count, second.People!.Count);
            Assert.Equal(first.People.Updated, second.People.Updated);
            Assert.Equal(first.People.People.Select(p => (p.Name, p.Agency, p.Station, p.Flags.Single().Code)),
                         second.People.People.Select(p => (p.Name, p.Agency, p.Station, p.Flags.Single().Code)));
        }
    }
}
