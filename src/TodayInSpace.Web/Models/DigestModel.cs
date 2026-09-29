using System.Text.Json.Serialization;

namespace TodayInSpace.Web.Models
{
    // Top-level digest object. Maps to digests.json in blob storage.
    public class DigestModel
    {
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("apod")]
        public ApodInfo? Apod { get; set; }

        [JsonPropertyName("spaceWeather")]
        public SpaceWeatherInfo? SpaceWeather { get; set; }
    }

    // Astronomy Picture of the Day section
    public class ApodInfo
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("copyright")]
        public string? Copyright { get; set; }
    }

    // Space weather section
    public class SpaceWeatherInfo
    {
        [JsonPropertyName("currentKp")]
        public int? CurrentKp { get; set; }

        [JsonPropertyName("auroraChance")]
        public string? AuroraChance { get; set; }

        [JsonPropertyName("solarWindSpeed")]
        public int? SolarWindSpeed { get; set; }

        [JsonPropertyName("forecast")]
        public List<ForecastDay>? Forecast { get; set; }
    }

    // One day of the 3-day Kp forecast
    public class ForecastDay
    {
        [JsonPropertyName("day")]
        public string? Day { get; set; }

        [JsonPropertyName("kp")]
        public int? Kp { get; set; }
    }
}