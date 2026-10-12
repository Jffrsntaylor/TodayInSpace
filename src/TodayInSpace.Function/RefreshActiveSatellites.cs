using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TodayInSpace.Core;

namespace TodayInSpace.Function
{
    // Downloads the orbital elements of every active satellite (~16,000) from CelesTrak and stores them for the
    // globe's point cloud. CelesTrak updates this about every 2 hours and blocks IPs that download the same data
    // too often, so this runs every 6 hours and the web app never calls CelesTrak for it.
    public class RefreshActiveSatellites
    {
        private static readonly HttpClient http = CreateClient();

        private readonly ILogger _logger;

        public RefreshActiveSatellites(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RefreshActiveSatellites>();
        }

        // What came back from CelesTrak. Body is null on any failure.
        public record TleResponse(HttpStatusCode? Status, string? Body);

        // Write is the snapshot to store, or null to leave the stored one alone.
        public record RunResult(ActiveSatellitesSnapshot? Write, string? Reason);

        // 01:50, 07:50, 13:50, 19:50 UTC: clear of the other refreshes at :15, :30 and :45 and of the digest.
        // No RunOnStartup: it would fire on every restart and scale-out, and CelesTrak would block us.
        [Function("RefreshActiveSatellites")]
        public async Task Run([TimerTrigger("0 50 1,7,13,19 * * *")] TimerInfo timer)
        {
            string connStr   = Environment.GetEnvironmentVariable("Storage__ConnectionString") ?? "";
            string container = Environment.GetEnvironmentVariable("Storage__ContainerName") ?? "digests";

            if (string.IsNullOrEmpty(connStr))
            {
                LogNotRefreshed("storage not configured", null);
                return;
            }

            var response = await FetchAsync();
            var run = Decide(response, DateTimeOffset.UtcNow);
            if (run.Write == null)
            {
                LogNotRefreshed(run.Reason ?? "unknown", response.Status);
                return;
            }

            try
            {
                var containerClient = new BlobContainerClient(connStr, container);
                await containerClient.CreateIfNotExistsAsync();
                await containerClient.GetBlobClient(ActiveSatellites.BlobName)
                    .UploadAsync(BinaryData.FromString(ActiveSatellites.Serialize(run.Write)), overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("active-tles.json write failed: {msg}", ex.Message);
                LogNotRefreshed("blob write failed", response.Status);
                return;
            }

            // One line per run with named properties, so App Insights can chart and alert on it.
            var groups = run.Write.Satellites.GroupBy(s => s.G).ToDictionary(g => g.Key, g => g.Count());
            _logger.LogInformation(
                "Active satellites refreshed total={Total} starlink={Starlink} navigation={Navigation} geo={Geo} other={Other}",
                run.Write.Satellites.Count, groups.GetValueOrDefault(ActiveSatellites.Starlink),
                groups.GetValueOrDefault(ActiveSatellites.Navigation), groups.GetValueOrDefault(ActiveSatellites.Geostationary),
                groups.GetValueOrDefault(ActiveSatellites.Other));
        }

        // The refresh decision, with the response passed in so tests can run it without CelesTrak or storage.
        // A bad download never overwrites the stored file: the globe keeps the last good one (TLEs stay usable for days).
        // minSatellites is only lowered by tests, so a small handmade file can stand in for the real one.
        public static RunResult Decide(TleResponse response, DateTimeOffset now, int minSatellites = ActiveSatellites.MinSatellites)
        {
            if (response.Body == null)
                return new RunResult(null, "fetch failed");

            var fresh = ActiveSatellites.Build(response.Body, now, minSatellites);
            if (fresh == null)
                return new RunResult(null, "too few satellites or unexpected format");

            return new RunResult(fresh, null);
        }

        // One GET, no retries: the next timer run is the retry, and retrying sooner risks a CelesTrak block.
        private async Task<TleResponse> FetchAsync()
        {
            try
            {
                using var response = await http.GetAsync(ActiveSatellites.Url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("CelesTrak active TLEs returned {Status}", (int)response.StatusCode);
                    return new TleResponse(response.StatusCode, null);
                }
                return new TleResponse(response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("CelesTrak active TLEs fetch failed: {msg}", ex.Message);
                return new TleResponse(null, null);
            }
        }

        private void LogNotRefreshed(string reason, HttpStatusCode? status)
        {
            _logger.LogWarning("Active satellites not refreshed reason={Reason} status={Status}", reason,
                status is { } s ? ((int)s).ToString() : "none");
        }

        private static HttpClient CreateClient()
        {
            // ~2.7 MB of text, so allow longer than the small API calls. Compressed on the wire when CelesTrak offers it.
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TodayInSpace/1.0 (+https://github.com/Jffrsntaylor/TodayInSpace)");
            return client;
        }
    }
}
