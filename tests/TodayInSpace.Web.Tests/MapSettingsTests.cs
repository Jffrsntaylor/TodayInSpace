using Microsoft.Extensions.Configuration;
using TodayInSpace.Web.Helpers;

namespace TodayInSpace.Web.Tests
{
    // The value Index.cshtml writes into the map's data-carto-key attribute.
    public class MapSettingsTests
    {
        private static IConfiguration Config(params (string Key, string? Value)[] values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
                .Build();

        [Fact]
        public void CartoKey_ReadsTheSetting()
        {
            Assert.Equal("abc123", MapSettings.CartoKey(Config(("Map:CartoKey", "abc123"))));
        }

        [Fact]
        public void CartoKey_TrimsStrayWhitespace()
        {
            // Easy to paste a trailing space or newline into the portal.
            Assert.Equal("abc123", MapSettings.CartoKey(Config(("Map:CartoKey", " abc123\n"))));
        }

        [Fact]
        public void CartoKey_IsEmptyWhenUnset()
        {
            Assert.Equal("", MapSettings.CartoKey(Config()));
            Assert.Equal("", MapSettings.CartoKey(Config(("Map:CartoKey", null))));
        }

        [Fact]
        public void CartoKey_ReadsTheAppSettingForm()
        {
            // Azure app settings use "__" for ":", which the environment variables provider maps back.
            Environment.SetEnvironmentVariable("TIS_TEST_Map__CartoKey", "from-env");
            try
            {
                var config = new ConfigurationBuilder().AddEnvironmentVariables("TIS_TEST_").Build();
                Assert.Equal("from-env", MapSettings.CartoKey(config));
            }
            finally
            {
                Environment.SetEnvironmentVariable("TIS_TEST_Map__CartoKey", null);
            }
        }
    }
}
