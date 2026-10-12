using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TodayInSpace.Core;

namespace TodayInSpace.Function
{
    // Counts the satellites in Earth orbit from CelesTrak's SATCAT file and stores the result for the web
    // app to serve. The file is ~7 MB and CelesTrak asks people not to hammer it, so this asks at most
    // 4 times a day and only downloads it when it has changed (usually it hasn't).
    public class RefreshSatellites
    {
        private static readonly HttpClient http = CreateClient();

        private readonly ILogger _logger;

        public RefreshSatellites(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RefreshSatellites>();
        }

        // What came back from CelesTrak. Body is null on a 304 or any failure.
        public record SatcatResponse(HttpStatusCode? Status, string? Body, DateTimeOffset? LastModified, string? ETag);

        // Write is the snapshot to store, or null to leave the stored one alone.
        public record RunResult(SatellitesSnapshot? Write, bool CatalogChanged, string? Reason);

        // Every 6 hours at :30 (00:30, 06:30, 12:30, 18:30 UTC), clear of people at :15 and launches at :45.
        // No RunOnStartup: it would fire on every restart and scale-out.
        [Function("RefreshSatellites")]
        public async Task Run([TimerTrigger("0 30 */6 * * *")] TimerInfo timer)
        {
            string connStr   = Environment.GetEnvironmentVariable("Storage__ConnectionString") ?? "";
            string container = Environment.GetEnvironmentVariable("Storage__ContainerName") ?? "digests";

            if (string.IsNullOrEmpty(connStr))
            {
                LogNotRefreshed("storage not configured", null);
                return;
            }

            var containerClient = new BlobContainerClient(connStr, container);
            var blob = containerClient.GetBlobClient(Satcat.BlobName);
            var previous = await ReadPreviousAsync(blob);

            var response = await FetchAsync(previous);
            var run = Decide(response, previous, DateTimeOffset.UtcNow);
            if (run.Write == null)
            {
                LogNotRefreshed(run.Reason ?? "unknown", response.Status);
                return;
            }

            try
            {
                await containerClient.CreateIfNotExistsAsync();
                await blob.UploadAsync(BinaryData.FromString(Satcat.Serialize(run.Write)), overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("satellites.json write failed: {msg}", ex.Message);
                LogNotRefreshed("blob write failed", response.Status);
                return;
            }

            // One line per run with named properties, so App Insights can chart and alert on it.
            if (run.CatalogChanged)
                _logger.LogInformation("Satellites refreshed catalog=new active={Active} inOrbit={InOrbit} starlink={Starlink}",
                    run.Write.Active, run.Write.InOrbit, run.Write.Starlink);
            else
                _logger.LogInformation("Satellites refreshed catalog=unchanged");
        }

        // Asks only for a newer file than the one we already counted. CelesTrak answers 304 if there isn't one.
        public static HttpRequestMessage BuildRequest(SatellitesSnapshot? previous)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Satcat.Url);
            if (!string.IsNullOrEmpty(previous?.ETag))
                request.Headers.TryAddWithoutValidation("If-None-Match", previous.ETag);
            if (previous?.CatalogUpdated is { } updated)
                request.Headers.IfModifiedSince = updated;
            return request;
        }

        // The refresh decisions, with the response passed in so tests can run them without CelesTrak or storage.
        // minRows is only lowered by tests, so a small handmade file can stand in for the real one.
        public static RunResult Decide(SatcatResponse response, SatellitesSnapshot? previous, DateTimeOffset now,
                                       int minRows = Satcat.MinRows)
        {
            if (response.Status == HttpStatusCode.NotModified)
            {
                // Same catalogue: keep the numbers, but say we checked, so the site's freshness line stays honest.
                return previous == null
                    ? new RunResult(null, false, "not modified but nothing stored")
                    : new RunResult(previous with { Checked = now }, false, null);
            }

            if (response.Body == null)
                return new RunResult(null, false, "fetch failed");

            var fresh = Satcat.Build(response.Body, response.LastModified, response.ETag, now, minRows);
            if (fresh == null)
                return new RunResult(null, false, "unexpected format");
            if (Satcat.LooksWrong(previous, fresh))
                return new RunResult(null, false,
                    $"looks wrong active={fresh.Active} inOrbit={fresh.InOrbit} previousActive={previous!.Active} previousInOrbit={previous.InOrbit}");

            return new RunResult(fresh, true, null);
        }

        // One GET, no retries: the next timer run is the retry.
        private async Task<SatcatResponse> FetchAsync(SatellitesSnapshot? previous)
        {
            try
            {
                using var request = BuildRequest(previous);
                using var response = await http.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return new SatcatResponse(response.StatusCode, null, null, null);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("CelesTrak SATCAT returned {Status}", (int)response.StatusCode);
                    return new SatcatResponse(response.StatusCode, null, null, null);
                }
                return new SatcatResponse(response.StatusCode, await response.Content.ReadAsStringAsync(),
                    response.Content.Headers.LastModified, response.Headers.ETag?.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("CelesTrak SATCAT fetch failed: {msg}", ex.Message);
                return new SatcatResponse(null, null, null, null);
            }
        }

        // The last snapshot, for the conditional request and to keep if this run fails. Null if there isn't one.
        private async Task<SatellitesSnapshot?> ReadPreviousAsync(BlobClient blob)
        {
            try
            {
                if (!await blob.ExistsAsync())
                    return null;
                return Satcat.Parse((await blob.DownloadContentAsync()).Value.Content.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Couldn't read the previous satellites.json: {msg}", ex.Message);
                return null;
            }
        }

        private void LogNotRefreshed(string reason, HttpStatusCode? status)
        {
            _logger.LogWarning("Satellites not refreshed reason={Reason} status={Status}", reason,
                status is { } s ? ((int)s).ToString() : "none");
        }

        private static HttpClient CreateClient()
        {
            // The file is ~7 MB, so allow longer than the LL2 calls.
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TodayInSpace/1.0 (+https://github.com/Jffrsntaylor/TodayInSpace)");
            return client;
        }
    }
}
