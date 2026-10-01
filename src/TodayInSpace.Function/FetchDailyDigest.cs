using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using TodayInSpace.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace TodayInSpace.Function
{
    public class FetchDailyDigest
    {
        private readonly ILogger _logger;
        private static readonly HttpClient http = new HttpClient();

        public FetchDailyDigest(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<FetchDailyDigest>();
        }

        // Runs on a schedule: 10:00 UTC, then again at 18:00 UTC in case NASA published late
        // or was unavailable in the morning (a second run just refreshes the same day's digest).
        // For TESTING you can change it to "0 */5 * * * *" (every 5 min) so you
        // don't have to wait a day to see it work, then change it back.
        [Function("FetchDailyDigest")]
        public async Task Run([TimerTrigger("0 0 10,18 * * *")] TimerInfo timer)
        {
            _logger.LogInformation("FetchDailyDigest started at {time}", DateTime.UtcNow);

            // ---- Read settings (set these in the Function App config) ----
            string nasaKey   = Environment.GetEnvironmentVariable("NASA_API_KEY") ?? "DEMO_KEY";
            string connStr   = Environment.GetEnvironmentVariable("Storage__ConnectionString") ?? "";
            string container = Environment.GetEnvironmentVariable("Storage__ContainerName") ?? "digests";

            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            if (string.IsNullOrEmpty(connStr))
            {
                LogNotPublished(today, "storage not configured", ApodSource.None);
                return;
            }

            var containerClient = new BlobContainerClient(connStr, container);

            // ---- 1. NASA APOD ----
            // If NASA's API is down, don't lose the whole day: still publish the space weather
            // and carry over the last good picture (its own "date" tells the site it's from earlier).
            string apodSource = ApodSource.Api;
            ApodInfo? apod = await FetchApodAsync(nasaKey, today);
            if (apod == null)
            {
                apodSource = ApodSource.Rss;
                apod = await FetchApodFromRssAsync(today);
            }
            bool apodIsFresh = apod != null;
            if (apod == null)
            {
                apod = await LoadPreviousApodAsync(containerClient, today);
                apodSource = apod != null ? ApodSource.CarriedOver : ApodSource.None;
                _logger.LogWarning(apod != null
                    ? "APOD unavailable; carrying over the picture from {date}."
                    : "APOD unavailable and no previous picture found; publishing space weather only.",
                    apod?.date);
            }

            // ---- 1b. Keep our own copy of the image ----
            // NASA's image URLs have changed before; the archive shouldn't depend on them.
            // If this fails the digest still publishes with NASA's link.
            if (apodIsFresh && apod != null && !string.IsNullOrEmpty(apod.imageUrl))
            {
                var images = ImageArchiver.GetContainer(connStr);
                // Overwrite: a re-run for the same day replaces a bad or earlier copy of today's image.
                apod.imageBlob = await ImageArchiver.ArchiveAsync(images, today, apod.imageUrl, _logger, overwrite: true);
            }

            // ---- 2. NOAA current Kp ----
            int? currentKp = null;
            try
            {
                string kpJson = await http.GetStringAsync(
                    "https://services.swpc.noaa.gov/products/noaa-planetary-k-index.json");
                using var doc = JsonDocument.Parse(kpJson);
                // Array of objects; take the last entry's "Kp" and round it.
                var arr = doc.RootElement;
                if (arr.GetArrayLength() > 0)
                {
                    var last = arr[arr.GetArrayLength() - 1];
                    if (last.TryGetProperty("Kp", out var kpEl))
                    {
                        double kpVal = kpEl.ValueKind == JsonValueKind.Number
                            ? kpEl.GetDouble()
                            : double.Parse(kpEl.GetString() ?? "0");
                        currentKp = (int)Math.Round(kpVal);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Kp fetch failed: {msg}. Continuing without it.", ex.Message);
            }

            // ---- 3. NOAA solar wind speed ----
            // NOAA retired products/solar-wind/plasma-1-day.json; the summary feed gives the
            // latest real-time proton speed: [{"proton_speed": 412, "time_tag": "..."}]
            int? solarWindSpeed = null;
            try
            {
                string windJson = await http.GetStringAsync(
                    "https://services.swpc.noaa.gov/products/summary/solar-wind-speed.json");
                solarWindSpeed = NoaaParsers.ParseSolarWindSpeed(windJson);
                if (solarWindSpeed == null)
                    _logger.LogWarning("Solar wind feed returned no usable speed.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Solar wind fetch failed: {msg}. Continuing without it.", ex.Message);
            }

            // ---- 4. NOAA 3-day Kp forecast (simple version) ----
            // We build a small 3-entry forecast from the forecast feed.
            var forecast = new System.Collections.Generic.List<ForecastDay>();
            try
            {
                string fcJson = await http.GetStringAsync(
                    "https://services.swpc.noaa.gov/products/noaa-planetary-k-index-forecast.json");
                using var doc = JsonDocument.Parse(fcJson);
                var entries = doc.RootElement;  // array of OBJECTS

                var seenDays = new System.Collections.Generic.HashSet<string>();
                // Walk forward, taking the highest Kp per day, up to 3 upcoming days.
                var dayMax = new System.Collections.Generic.Dictionary<string, (DateTime dt, int kp)>();

                for (int i = 0; i < entries.GetArrayLength(); i++)
                {
                    var entry = entries[i];

                    string timeTag = entry.TryGetProperty("time_tag", out var tEl)
                        ? tEl.GetString() ?? "" : "";

                    double kpVal = 0;
                    if (entry.TryGetProperty("kp", out var kEl))
                    {
                        kpVal = kEl.ValueKind == JsonValueKind.Number
                            ? kEl.GetDouble()
                            : double.TryParse(kEl.GetString(), out var p) ? p : 0;
                    }

                    if (DateTime.TryParse(timeTag, out var dt))
                    {
                        // only future days
                        if (dt.Date < DateTime.UtcNow.Date) continue;

                        string dayKey = dt.ToString("yyyy-MM-dd");
                        int rounded = (int)Math.Round(kpVal);
                        if (!dayMax.ContainsKey(dayKey) || rounded > dayMax[dayKey].kp)
                            dayMax[dayKey] = (dt, rounded);
                    }
                }

                forecast = dayMax
                    .OrderBy(kvp => kvp.Value.dt)
                    .Take(3)
                    .Select(kvp => new ForecastDay
                    {
                        day = kvp.Value.dt.ToString("ddd"),
                        kp = kvp.Value.kp
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Forecast fetch failed: {msg}. Continuing without it.", ex.Message);
            }

            // ---- 5. Build the digest object (matches the website's schema) ----
            var digest = new DigestModel
            {
                date = today,
                apod = apod,
                spaceWeather = new SpaceWeatherInfo
                {
                    currentKp      = currentKp,
                    auroraChance   = AuroraFromKp(currentKp),
                    solarWindSpeed = solarWindSpeed,
                    forecast       = forecast
                }
            };

            string digestJson = JsonSerializer.Serialize(digest, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            // ---- 6. Write two blobs: today's dated file AND latest.json ----
            try
            {
                await containerClient.CreateIfNotExistsAsync();

                await UploadAsync(containerClient, $"{today}.json", digestJson);
                await UploadAsync(containerClient, "latest.json", digestJson);

                // One line per run with named properties, so App Insights can chart and alert on it.
                _logger.LogInformation(
                    "Digest published {Date} apodSource={ApodSource} imageArchived={ImageArchived} kp={Kp} solarWind={SolarWind}",
                    today, apodSource, apod?.imageBlob != null, currentKp, solarWindSpeed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Blob write failed: {msg}", ex.Message);
                LogNotPublished(today, "blob write failed", apodSource);
            }
        }

        // Where today's picture came from, as it appears in the run summary.
        private static class ApodSource
        {
            public const string Api = "api";
            public const string Rss = "rss";
            public const string CarriedOver = "carried-over";
            public const string None = "none";
        }

        // Error level on purpose: an alert fires on this line, because a day without a digest
        // is exactly what went unnoticed during past NASA outages.
        private void LogNotPublished(string date, string reason, string apodSource)
        {
            _logger.LogError("Digest not published {Date} reason={Reason} apodSource={ApodSource}",
                date, reason, apodSource);
        }

        // Calls NASA's APOD API, trying twice with a short pause. Returns null if it's unavailable.
        private async Task<ApodInfo?> FetchApodAsync(string nasaKey, string today)
        {
            string apodUrl = $"https://api.nasa.gov/planetary/apod?api_key={nasaKey}&thumbs=true";
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    string apodJson = await http.GetStringAsync(apodUrl);
                    using var doc = JsonDocument.Parse(apodJson);
                    var root = doc.RootElement;

                    // APOD is sometimes a video: keep its URL (the site embeds known players/files)
                    // and the thumbnail NASA provides with thumbs=true.
                    bool isVideo = GetString(root, "media_type") == "video";
                    string imageUrl = GetString(root, "media_type") == "image" ? GetString(root, "url") : "";
                    string apodDate = GetString(root, "date");
                    string title = GetString(root, "title");

                    // Since NASA's site move the API sometimes answers with placeholder data
                    // (title "NASA Science" + the NASA logo). Treat that as "unavailable".
                    if (!ApodSources.LooksLikeRealApod(title, GetString(root, "explanation"), imageUrl))
                    {
                        _logger.LogWarning("APOD API returned placeholder data (title '{title}', image {url}); ignoring it.", title, imageUrl);
                        return null;
                    }

                    return new ApodInfo
                    {
                        title          = title,
                        explanation    = GetString(root, "explanation"),
                        imageUrl       = imageUrl,
                        sourceImageUrl = imageUrl,
                        videoUrl       = isVideo ? GetString(root, "url") : "",
                        thumbnailUrl   = isVideo ? GetString(root, "thumbnail_url") : "",
                        copyright      = GetString(root, "copyright"),
                        date           = string.IsNullOrEmpty(apodDate) ? today : apodDate
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("APOD fetch attempt {attempt} failed: {msg}", attempt, ex.Message);
                    if (attempt == 1) await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
            return null;
        }

        // Backup source: NASA's own APOD RSS feed on science.nasa.gov. Returns null if today's
        // entry isn't in the feed yet or the feed can't be read.
        private async Task<ApodInfo?> FetchApodFromRssAsync(string today)
        {
            try
            {
                string xml = await http.GetStringAsync("https://science.nasa.gov/feed/apod-basic/");
                var entry = ApodSources.ParseRssForDate(xml, DateOnly.ParseExact(today, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                if (entry == null)
                {
                    _logger.LogWarning("APOD RSS feed has no entry for {today} yet.", today);
                    return null;
                }

                _logger.LogInformation("Using the APOD RSS feed for {today}: {title}", today, entry.Title);
                var apod = new ApodInfo
                {
                    title          = entry.Title,
                    explanation    = entry.Explanation,
                    imageUrl       = entry.ImageUrl,
                    sourceImageUrl = entry.ImageUrl,
                    copyright      = entry.Copyright,
                    date           = entry.Date
                };

                // The feed doesn't say whether the day is a video. The article page does (og:video),
                // so check it; on video days show the video with NASA's snapshot as the thumbnail.
                if (Uri.TryCreate(entry.ArticleUrl, UriKind.Absolute, out var article) &&
                    article.Host.EndsWith("science.nasa.gov", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var media = ApodSources.ParseArticleMedia(await http.GetStringAsync(article));
                        if (!string.IsNullOrEmpty(media.VideoUrl))
                        {
                            apod.videoUrl = media.VideoUrl;
                            apod.thumbnailUrl = media.ImageUrl;
                            apod.imageUrl = apod.sourceImageUrl = "";
                            _logger.LogInformation("{today} is a video day: {video}", today, media.VideoUrl);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Couldn't check the APOD article page for a video: {msg}", ex.Message);
                    }
                }
                return apod;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("APOD RSS fetch failed: {msg}", ex.Message);
                return null;
            }
        }

        // Finds the most recent good picture to show again during a NASA outage: latest.json first,
        // then the past week's dated digests. Skips placeholder entries (e.g. the "NASA Science" logo).
        private async Task<ApodInfo?> LoadPreviousApodAsync(BlobContainerClient containerClient, string today)
        {
            var candidates = new List<string> { "latest.json" };
            var day = DateOnly.ParseExact(today, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 1; i <= 7; i++)
                candidates.Add(day.AddDays(-i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".json");

            foreach (var name in candidates)
            {
                try
                {
                    var blob = containerClient.GetBlobClient(name);
                    if (!await blob.ExistsAsync())
                        continue;

                    var previous = JsonSerializer.Deserialize<DigestModel>((await blob.DownloadContentAsync()).Value.Content.ToString());
                    var apod = previous?.apod;
                    if (apod == null ||
                        !ApodSources.LooksLikeRealApod(apod.title, apod.explanation,
                            string.IsNullOrEmpty(apod.sourceImageUrl) ? apod.imageUrl : apod.sourceImageUrl))
                        continue;

                    // Older digests didn't store the picture's own date; use the digest's date.
                    if (string.IsNullOrEmpty(apod.date))
                        apod.date = previous!.date;
                    return apod;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Couldn't read {name}: {msg}", name, ex.Message);
                }
            }
            return null;
        }

        // Turn a Kp number into the aurora label the website expects.
        private static string AuroraFromKp(int? kp)
        {
            if (kp == null) return "UNAVAILABLE";
            if (kp < 4) return "UNLIKELY";
            if (kp < 6) return "POSSIBLE";
            if (kp < 8) return "LIKELY";
            return "STRONG";
        }

        private static string GetString(JsonElement el, string prop)
        {
            return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        }

        private static async Task UploadAsync(BlobContainerClient container, string name, string content)
        {
            var blob = container.GetBlobClient(name);
            using var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            await blob.UploadAsync(ms, overwrite: true);
        }
    }

    // ---- Models (match the website's digest schema) ----
    public class DigestModel
    {
        public string date { get; set; } = "";
        public ApodInfo? apod { get; set; }
        public SpaceWeatherInfo spaceWeather { get; set; } = new();
    }
    public class ApodInfo
    {
        public string title { get; set; } = "";
        public string explanation { get; set; } = "";
        public string imageUrl { get; set; } = "";
        public string copyright { get; set; } = "";
        // The date NASA published this picture. Differs from the digest date when
        // NASA was unavailable and the previous picture was carried over.
        public string date { get; set; } = "";
        // Name of our archived copy in the images container (null if not archived).
        public string? imageBlob { get; set; }
        // NASA's original image URL, kept for reference/credit.
        public string sourceImageUrl { get; set; } = "";
        // Video days: the video (YouTube/Vimeo/NASA .mp4) and a still for previews.
        public string videoUrl { get; set; } = "";
        public string thumbnailUrl { get; set; } = "";
    }
    public class SpaceWeatherInfo
    {
        public int? currentKp { get; set; }
        public string auroraChance { get; set; } = "";
        public int? solarWindSpeed { get; set; }
        public System.Collections.Generic.List<ForecastDay> forecast { get; set; } = new();
    }
    public class ForecastDay
    {
        public string day { get; set; } = "";
        public int? kp { get; set; }
    }
}
