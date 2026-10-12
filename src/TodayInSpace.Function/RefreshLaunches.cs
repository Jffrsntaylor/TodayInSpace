using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TodayInSpace.Core;

namespace TodayInSpace.Function
{
    // Fetches the next launches and this year's orbital launch count from The Space Devs (LL2) and stores
    // them for the web app to serve. Same reasons as RefreshPeopleInSpace: LL2 allows about 15 requests an
    // hour per IP, so the web app never calls it. A normal hour here is 1 call; one that re-counts the
    // year is 1 + up to 4 pages.
    public class RefreshLaunches
    {
        private static readonly HttpClient http = CreateClient();

        private readonly ILogger _logger;

        public RefreshLaunches(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RefreshLaunches>();
        }

        // What one run fetched, for the log line. Refresh is null when nothing new came in.
        public record RunResult(LaunchesRefresh? Refresh, YearStatsBuild? Stats, int Pages, string? StatsError);

        // Hourly at :45, because launch times slip and scrub often. People runs at :15, so the two never
        // overlap. No RunOnStartup: it would fire on every restart and scale-out and spend LL2's allowance.
        [Function("RefreshLaunches")]
        public async Task Run([TimerTrigger("0 45 * * * *")] TimerInfo timer)
        {
            string connStr   = Environment.GetEnvironmentVariable("Storage__ConnectionString") ?? "";
            string container = Environment.GetEnvironmentVariable("Storage__ContainerName") ?? "digests";

            if (string.IsNullOrEmpty(connStr))
            {
                LogNotRefreshed("storage not configured");
                return;
            }

            var containerClient = new BlobContainerClient(connStr, container);
            var blob = containerClient.GetBlobClient(Launches.BlobName);
            var now = DateTimeOffset.UtcNow;

            var run = await RunAsync(FetchAsync, await ReadPreviousAsync(blob), now);
            if (run.Refresh == null)
            {
                LogNotRefreshed(run.StatsError == null ? "upcoming fetch failed" : "upcoming fetch failed, " + run.StatsError);
                return;
            }
            if (run.StatsError != null)
                _logger.LogWarning("Year count not refreshed ({Error}); keeping the previous one.", run.StatsError);

            try
            {
                await containerClient.CreateIfNotExistsAsync();
                await blob.UploadAsync(BinaryData.FromString(Launches.Serialize(run.Refresh.Snapshot)), overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("launches.json write failed: {msg}", ex.Message);
                LogNotRefreshed("blob write failed");
                return;
            }

            if (run.Stats is { Difference: not 0 } built)
                _logger.LogWarning("Launch count differs from LL2: counted {Total}, LL2 says {Ll2Count}.",
                    built.Stats.Total, built.Ll2Count);

            // One line per run with named properties, so App Insights can chart and alert on it.
            var stats = run.Refresh.Snapshot.Stats;
            _logger.LogInformation(
                "Launches refreshed stats={StatsFrom} upcoming={UpcomingFrom} year={Year} total={Total} pages={Pages} llCount={LlCount}",
                run.Refresh.StatsFrom, run.Refresh.UpcomingFrom, stats?.Year, stats?.Total, run.Pages, run.Stats?.Ll2Count);
        }

        // The refresh decisions, with the network passed in so tests can run them without LL2 or storage.
        // fetch returns the body, or null on any failure.
        public static async Task<RunResult> RunAsync(
            Func<string, Task<(string? Body, HttpStatusCode? Status)>> fetch, LaunchesSnapshot? previous, DateTimeOffset now)
        {
            var upcoming = await fetch(Launches.UpcomingUrl);
            var freshUpcoming = Launches.ParseUpcoming(upcoming.Body);

            YearStatsBuild? stats = null;
            string? statsError = null;
            int pages = 0;
            if (Launches.ShouldRefreshStats(previous, now))
            {
                int year = now.UtcDateTime.Year;
                var fetched = new List<Ll2LaunchPage>();
                string? url = Launches.YearUrl(year);
                // All pages or none: a partial count would look like a real (too low) number.
                while (url != null)
                {
                    if (fetched.Count == Launches.MaxYearPages)
                    {
                        statsError = "too many year pages";
                        break;
                    }
                    var page = LaunchesParser.ParsePage((await fetch(url)).Body);
                    pages++;
                    if (page == null)
                    {
                        statsError = "year page fetch failed";
                        break;
                    }
                    fetched.Add(page);
                    url = page.Next;
                    // Only follow links back to LL2 itself.
                    if (url != null && !Launches.IsLl2Url(url))
                    {
                        statsError = "unexpected next link";
                        break;
                    }
                }
                // January 1 gives an empty first page; that's a real count of 0, not a failure.
                if (statsError == null)
                    stats = Launches.BuildStats(year, fetched, now);
            }

            var refresh = Launches.Combine(previous, stats?.Stats, freshUpcoming, now);
            return new RunResult(refresh, stats, pages, statsError);
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

        // The last snapshot, so a failed fetch keeps what we had. Null if there isn't one.
        private async Task<LaunchesSnapshot?> ReadPreviousAsync(BlobClient blob)
        {
            try
            {
                if (!await blob.ExistsAsync())
                    return null;
                return Launches.Parse((await blob.DownloadContentAsync()).Value.Content.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Couldn't read the previous launches.json: {msg}", ex.Message);
                return null;
            }
        }

        private void LogNotRefreshed(string reason)
        {
            _logger.LogWarning("Launches not refreshed reason={Reason}", reason);
        }

        private static HttpClient CreateClient()
        {
            // Year pages are ~1 MB each in mode=normal and LL2 has taken over 30 seconds to send one.
            // 60 keeps a worst-case run (upcoming + 4 pages) inside Consumption's 5-minute limit.
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TodayInSpace/1.0 (+https://github.com/Jffrsntaylor/TodayInSpace)");
            return client;
        }
    }
}
