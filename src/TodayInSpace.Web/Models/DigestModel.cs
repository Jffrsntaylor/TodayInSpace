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

        // Our archived copy in the "images" container, e.g. "2026-09-28.jpg".
        // Older digests won't have this until the backfill runs.
        [JsonPropertyName("imageBlob")]
        public string? ImageBlob { get; set; }

        // NASA's original image URL.
        [JsonPropertyName("sourceImageUrl")]
        public string? SourceImageUrl { get; set; }

        // The date NASA published this picture. Older digests don't have it.
        // If it differs from the digest's date, NASA was down and the last good picture was reused.
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        // Video days: the video URL and a still image. Older digests don't have these.
        [JsonPropertyName("videoUrl")]
        public string? VideoUrl { get; set; }

        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrl { get; set; }
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