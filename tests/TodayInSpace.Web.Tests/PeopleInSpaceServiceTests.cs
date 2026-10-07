using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TodayInSpace.Core;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    public class PeopleInSpaceServiceTests
    {
        // What the function writes to sky/people.json.
        private const string Stored = @"{""count"":2,""updated"":""2026-10-07T12:15:00+00:00"",""source"":""The Space Devs"",""people"":[
            {""name"":""Jessica Meir"",""agency"":""NASA"",""flags"":[{""code"":""US"",""country"":""United States of America""}],""station"":""ISS""},
            {""name"":""Luke Delaney"",""agency"":""NASA"",""flags"":[{""code"":""US"",""country"":""United States of America""}],""station"":""In orbit""}
        ]}";

        // Returns canned blob contents and counts reads.
        private class FakeStore : IPeopleStore
        {
            public string? Json { get; init; }
            public int Reads { get; private set; }

            public Task<string?> ReadAsync()
            {
                Reads++;
                return Task.FromResult(Json);
            }
        }

        // Fails the test if anything tries to go upstream: the web app must not call LL2.
        private class NoNetwork : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("Unexpected HTTP call to " + request.RequestUri);
        }

        private static SkyDataService Service(FakeStore store) =>
            new(new HttpClient(new NoNetwork()), store, new MemoryCache(new MemoryCacheOptions()), NullLogger<SkyDataService>.Instance);

        [Fact]
        public async Task GetPeople_ReadsStoredSnapshot_WithoutCallingLl2()
        {
            var people = (await Service(new FakeStore { Json = Stored }).GetPeopleAsync())!;

            Assert.Equal(2, people.Count);
            Assert.Equal(new DateTimeOffset(2026, 10, 7, 12, 15, 0, TimeSpan.Zero), people.Updated);
            Assert.Equal("ISS", people.People.Single(p => p.Name == "Jessica Meir").Station);
            Assert.Equal("In orbit", people.People.Single(p => p.Name == "Luke Delaney").Station);
        }

        [Fact]
        public async Task GetPeople_ServesTheShapeTheFrontEndUses()
        {
            var people = await Service(new FakeStore { Json = Stored }).GetPeopleAsync();

            // Same options the controller's Ok() uses.
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(people, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var root = doc.RootElement;
            Assert.Equal(2, root.GetProperty("count").GetInt32());
            Assert.Equal(PeopleInSpaceSnapshot.Source, root.GetProperty("source").GetString());
            Assert.True(root.TryGetProperty("updated", out _));
            var first = root.GetProperty("people")[0];
            Assert.Equal("Jessica Meir", first.GetProperty("name").GetString());
            Assert.Equal("NASA", first.GetProperty("agency").GetString());
            Assert.Equal("ISS", first.GetProperty("station").GetString());
            Assert.Equal("US", first.GetProperty("flags")[0].GetProperty("code").GetString());
            Assert.Equal("United States of America", first.GetProperty("flags")[0].GetProperty("country").GetString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not json")]
        public async Task GetPeople_NoUsableSnapshot_ReturnsNull(string? json)
        {
            Assert.Null(await Service(new FakeStore { Json = json }).GetPeopleAsync());
        }

        [Fact]
        public async Task GetPeople_SecondCall_IsServedFromCache()
        {
            var store = new FakeStore { Json = Stored };
            var service = Service(store);

            await service.GetPeopleAsync();
            await service.GetPeopleAsync();

            Assert.Equal(1, store.Reads);
        }
    }
}
