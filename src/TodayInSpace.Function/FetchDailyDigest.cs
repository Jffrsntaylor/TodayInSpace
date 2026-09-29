using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
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

        // Runs on a schedule. The CRON below = once a day at 10:00 UTC.
        // For TESTING you can change it to "0 */5 * * * *" (every 5 min) so you
        // don't have to wait a day to see it work, then change it back.
        [Function("FetchDailyDigest")]
        public async Task Run([TimerTrigger("0 0 10 * * *")] TimerInfo timer)
        {
            _logger.LogInformation("FetchDailyDigest started at {time}", DateTime.UtcNow);

            // ---- Read settings (set these in the Function App config) ----
            string nasaKey   = Environment.GetEnvironmentVariable("NASA_API_KEY") ?? "DEMO_KEY";
            string connStr   = Environment.GetEnvironmentVariable("Storage__ConnectionString") ?? "";
            string container = Environment.GetEnvironmentVariable("Storage__ContainerName") ?? "digests";

            if (string.IsNullOrEmpty(connStr))
            {
                _logger.LogError("Storage connection string missing. Aborting.");
                return;
            }

            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // ---- 1. NASA APOD ----
            ApodInfo apod;
            try
            {
                string apodUrl = $"https://api.nasa.gov/planetary/apod?api_key={nasaKey}";
                string apodJson = await http.GetStringAsync(apodUrl);
                using var doc = JsonDocument.Parse(apodJson);
                var root = doc.RootElement;

                string mediaType = GetString(root, "media_type");
                // APOD is sometimes a video. If so, we still keep title/explanation
                // but leave the image URL empty (the UI shows a graceful fallback).
                string imageUrl = mediaType == "image" ? GetString(root, "url") : "";

                apod = new ApodInfo
                {
                    title       = GetString(root, "title"),
                    explanation = GetString(root, "explanation"),
                    imageUrl    = imageUrl,
                    copyright   = GetString(root, "copyright")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError("APOD fetch failed: {msg}. Aborting (no headline image).", ex.Message);
                return;  // APOD is the headline; if it fails, skip today's digest
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

            // ---- 3. NOAA solar wind speed (plasma) ----
            // plasma-1-day.json is array-of-arrays; row 0 is the header.
            // Columns: ["time_tag","density","speed","temperature"]
            int? solarWindSpeed = null;
            try
            {
                string plasmaJson = await http.GetStringAsync(
                    "https://services.swpc.noaa.gov/products/solar-wind/plasma-1-day.json");
                using var doc = JsonDocument.Parse(plasmaJson);
                var rows = doc.RootElement;
                if (rows.GetArrayLength() > 1)
                {
                    var lastRow = rows[rows.GetArrayLength() - 1];
                    // index 2 = speed
                    string speedStr = lastRow[2].GetString() ?? "";
                    if (double.TryParse(speedStr, out double spd))
                        solarWindSpeed = (int)Math.Round(spd);
                }
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
                var containerClient = new BlobContainerClient(connStr, container);
                await containerClient.CreateIfNotExistsAsync();

                await UploadAsync(containerClient, $"{today}.json", digestJson);
                await UploadAsync(containerClient, "latest.json", digestJson);

                _logger.LogInformation("Digest written for {today} (and latest.json).", today);
            }
            catch (Exception ex)
            {
                _logger.LogError("Blob write failed: {msg}", ex.Message);
            }
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
        public ApodInfo apod { get; set; } = new();
        public SpaceWeatherInfo spaceWeather { get; set; } = new();
    }
    public class ApodInfo
    {
        public string title { get; set; } = "";
        public string explanation { get; set; } = "";
        public string imageUrl { get; set; } = "";
        public string copyright { get; set; } = "";
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
