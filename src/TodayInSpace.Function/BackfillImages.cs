using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace TodayInSpace.Function
{
    // One-time (and safe to re-run) job: copies the image for every past digest
    // into our own storage and records it in that day's JSON.
    //
    // Protected by a function key. Processes a batch per call so it stays well under
    // the HTTP timeout; call it again until "remaining" is 0.
    //   GET /api/backfill-images?code=<function key>&max=20
    public class BackfillImages
    {
        private static readonly Regex DatedDigest = new(@"^(\d{4}-\d{2}-\d{2})\.json$", RegexOptions.Compiled);
        private readonly ILogger _logger;

        public BackfillImages(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<BackfillImages>();
        }

        [Function("BackfillImages")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "backfill-images")] HttpRequest req)
        {
            string connStr = Environment.GetEnvironmentVariable("Storage__ConnectionString") ?? "";
            string containerName = Environment.GetEnvironmentVariable("Storage__ContainerName") ?? "digests";
            if (string.IsNullOrEmpty(connStr))
                return new ObjectResult(new { error = "Storage connection string missing." }) { StatusCode = 500 };

            int max = int.TryParse(req.Query["max"], out var m) && m > 0 ? Math.Min(m, 100) : 20;

            var digests = new BlobContainerClient(connStr, containerName);
            var images = ImageArchiver.GetContainer(connStr);

            var archived = new List<string>();
            var failed = new List<string>();
            int alreadyDone = 0, noImage = 0, remaining = 0;
            int processed = 0;

            // Newest first, so latest.json-era days are covered first.
            var names = new List<string>();
            await foreach (var item in digests.GetBlobsAsync(traits: BlobTraits.None))
                if (DatedDigest.IsMatch(item.Name))
                    names.Add(item.Name);
            names.Sort(StringComparer.Ordinal);
            names.Reverse();

            foreach (var name in names)
            {
                string date = DatedDigest.Match(name).Groups[1].Value;
                var blob = digests.GetBlobClient(name);
                var root = JsonNode.Parse((await blob.DownloadContentAsync()).Value.Content.ToString()) as JsonObject;
                var apod = root?["apod"] as JsonObject;

                if (apod == null || string.IsNullOrEmpty((string?)apod["imageUrl"]))
                {
                    noImage++;
                    continue;
                }
                if (!string.IsNullOrEmpty((string?)apod["imageBlob"]))
                {
                    alreadyDone++;
                    continue;
                }
                if (processed >= max)
                {
                    remaining++;
                    continue;
                }

                processed++;
                string imageUrl = (string)apod["imageUrl"]!;
                string? blobName = await ImageArchiver.ArchiveAsync(images, date, imageUrl, _logger);
                if (blobName == null)
                {
                    failed.Add(date);
                    continue;
                }

                apod["imageBlob"] = blobName;
                apod["sourceImageUrl"] ??= imageUrl;
                await blob.UploadAsync(BinaryData.FromString(root!.ToJsonString(Indented)), overwrite: true);
                archived.Add(date);
            }

            // Keep latest.json in step with its dated copy.
            await SyncLatestAsync(digests);

            return new OkObjectResult(new
            {
                archived = archived.Count,
                alreadyDone,
                noImage,
                failed,
                remaining,
                next = remaining > 0 ? "Call again to continue." : "Done."
            });
        }

        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        private static async Task SyncLatestAsync(BlobContainerClient digests)
        {
            var latest = digests.GetBlobClient("latest.json");
            if (!await latest.ExistsAsync())
                return;

            var latestRoot = JsonNode.Parse((await latest.DownloadContentAsync()).Value.Content.ToString()) as JsonObject;
            string? date = (string?)latestRoot?["date"];
            if (string.IsNullOrEmpty(date) || latestRoot?["apod"]?["imageBlob"] != null)
                return;

            var dated = digests.GetBlobClient(date + ".json");
            if (!await dated.ExistsAsync())
                return;

            var datedContent = (await dated.DownloadContentAsync()).Value.Content;
            var datedRoot = JsonNode.Parse(datedContent.ToString());
            if (datedRoot?["apod"]?["imageBlob"] != null)
                await latest.UploadAsync(datedContent, overwrite: true);
        }
    }
}
