using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TodayInSpace.Core;

namespace TodayInSpace.Function
{
    // Fetches who's in space from The Space Devs (LL2) and stores it for the web app to serve.
    // LL2's free tier allows about 15 requests an hour per IP, and App Service outbound IPs are shared,
    // so fetching from the web app on demand got throttled. Here it's 2 calls every 3 hours (16 a day).
    public class RefreshPeopleInSpace
    {
        private const string PeopleUrl = "https://ll.thespacedevs.com/2.3.0/astronauts/?in_space=true&limit=100";
        // Only used to label which station each person is on.
        private const string ExpeditionsUrl = "https://ll.thespacedevs.com/2.3.0/expeditions/?is_active=true&mode=detailed";

        // mode=detailed is large and can be slow, so allow more than the default page-view timeout.
        private static readonly HttpClient http = CreateClient();

        private readonly ILogger _logger;

        public RefreshPeopleInSpace(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RefreshPeopleInSpace>();
        }

        // Every 3 hours at quarter past, so it never lines up with the digest runs at 10:00 and 18:00.
        // No RunOnStartup: it would fire on every restart and scale-out and spend LL2's allowance.
        [Function("RefreshPeopleInSpace")]
        public async Task Run([TimerTrigger("0 15 */3 * * *")] TimerInfo timer)
        {
            string connStr   = Environment.GetEnvironmentVariable("Storage__ConnectionString") ?? "";
            string container = Environment.GetEnvironmentVariable("Storage__ContainerName") ?? "digests";

            if (string.IsNullOrEmpty(connStr))
            {
                LogNotRefreshed("storage not configured", null);
                return;
            }

            // Astronauts first: if that fails there's nothing to write, so don't spend a second call.
            var people = await FetchAsync(PeopleUrl);
            if (people.Body == null)
            {
                LogNotRefreshed("astronauts fetch failed", people.Status);
                return;
            }

            var expeditions = await FetchAsync(ExpeditionsUrl);

            var containerClient = new BlobContainerClient(connStr, container);
            var blob = containerClient.GetBlobClient(PeopleInSpaceSnapshot.BlobName);
            var refresh = PeopleInSpaceSnapshot.Build(people.Body, expeditions.Body, await ReadPreviousAsync(blob), DateTimeOffset.UtcNow);
            if (refresh == null)
            {
                LogNotRefreshed("astronauts unexpected format", people.Status);
                return;
            }
            if (refresh.Stations != PeopleInSpaceSnapshot.StationsFresh)
                _logger.LogWarning("Expeditions unavailable (status {Status}); station labels: {Stations}.",
                    StatusText(expeditions.Status), refresh.Stations);

            try
            {
                await containerClient.CreateIfNotExistsAsync();
                await blob.UploadAsync(BinaryData.FromString(PeopleInSpaceSnapshot.Serialize(refresh.Snapshot)), overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("people.json write failed: {msg}", ex.Message);
                LogNotRefreshed("blob write failed", people.Status);
                return;
            }

            // One line per run with named properties, so App Insights can chart and alert on it.
            _logger.LogInformation("People refreshed count={Count} stations={Stations}",
                refresh.Snapshot.Count, refresh.Stations);
        }

        // One GET, no retries: on a 429 the next timer run is the retry. Body is null on any failure.
        private async Task<(string? Body, HttpStatusCode? Status)> FetchAsync(string url)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                    return (await response.Content.ReadAsStringAsync(), response.StatusCode);

                // Status and Retry-After make a rate limit (429) easy to tell apart from an outage.
                _logger.LogWarning("LL2 {Url} returned {Status} retryAfter={RetryAfter}",
                    url, (int)response.StatusCode, response.Headers.RetryAfter?.ToString() ?? "none");
                return (null, response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("LL2 {Url} fetch failed: {msg}", url, ex.Message);
                return (null, null);
            }
        }

        // The last snapshot, so station labels survive an expeditions outage. Null if there isn't one.
        private async Task<PeopleInSpace?> ReadPreviousAsync(BlobClient blob)
        {
            try
            {
                if (!await blob.ExistsAsync())
                    return null;
                return PeopleInSpaceSnapshot.Parse((await blob.DownloadContentAsync()).Value.Content.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Couldn't read the previous people.json: {msg}", ex.Message);
                return null;
            }
        }

        private void LogNotRefreshed(string reason, HttpStatusCode? status)
        {
            _logger.LogWarning("People not refreshed reason={Reason} status={Status}", reason, StatusText(status));
        }

        // "none" when the request never got a response (timeout, DNS, etc.).
        private static string StatusText(HttpStatusCode? status) => status is { } s ? ((int)s).ToString() : "none";

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TodayInSpace/1.0 (+https://github.com/Jffrsntaylor/TodayInSpace)");
            return client;
        }
    }
}
