using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    public class PeopleInSpaceServiceTests
    {
        private const string Astronauts = @"{""results"": [
            {""id"": 573, ""name"": ""Jessica Meir"", ""type"": {""name"": ""Government""}, ""agency"": {""abbrev"": ""NASA""},
             ""nationality"": [{""name"": ""United States of America"", ""alpha_2_code"": ""US""}]},
            {""id"": 710, ""name"": ""Luke Delaney"", ""type"": {""name"": ""Government""}, ""agency"": {""abbrev"": ""NASA""},
             ""nationality"": [{""name"": ""United States of America"", ""alpha_2_code"": ""US""}]},
            {""id"": 638, ""name"": ""Starman"", ""type"": {""name"": ""Non-Human""}, ""nationality"": []}
        ]}";

        private const string Expeditions = @"{""results"": [
            {""start"": ""2026-07-26T07:02:00Z"", ""spacestation"": {""name"": ""International Space Station""},
             ""crew"": [{""id"": 5279, ""astronaut"": {""id"": 573}}]}
        ]}";

        // Answers LL2 URLs from canned responses and counts how many requests went upstream.
        private class FakeLl2 : HttpMessageHandler
        {
            public bool ExpeditionsFail { get; init; }
            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                string path = request.RequestUri!.AbsolutePath;
                var response = path.Contains("/astronauts/") ? Ok(Astronauts)
                    : path.Contains("/expeditions/") && !ExpeditionsFail ? Ok(Expeditions)
                    : new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                return Task.FromResult(response);
            }

            private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }

        private static SkyDataService Service(FakeLl2 handler) =>
            new(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<SkyDataService>.Instance);

        [Fact]
        public async Task GetPeople_JoinsStations_AndUnmatchedPeopleAreInOrbit()
        {
            var people = (await Service(new FakeLl2()).GetPeopleAsync())!;

            Assert.Equal(2, people.Count);
            Assert.Equal("ISS", people.People.Single(p => p.Name == "Jessica Meir").Station);
            Assert.Equal("In orbit", people.People.Single(p => p.Name == "Luke Delaney").Station);
            Assert.False(people.StationsMissing);
        }

        [Fact]
        public async Task GetPeople_ExpeditionsFail_EveryoneInOrbit_CountUnchanged()
        {
            var people = (await Service(new FakeLl2 { ExpeditionsFail = true }).GetPeopleAsync())!;

            Assert.Equal(2, people.Count);
            Assert.Equal(2, people.People.Count);
            Assert.All(people.People, p => Assert.Equal("In orbit", p.Station));
            Assert.True(people.StationsMissing);
        }

        [Fact]
        public async Task GetPeople_SecondCall_IsServedFromCache()
        {
            var ll2 = new FakeLl2();
            var service = Service(ll2);

            await service.GetPeopleAsync();
            await service.GetPeopleAsync();

            Assert.Equal(2, ll2.Calls);
        }
    }
}
