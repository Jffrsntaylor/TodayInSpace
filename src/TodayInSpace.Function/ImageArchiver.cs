using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using TodayInSpace.Core;

namespace TodayInSpace.Function
{
    // Copies an APOD image from NASA into our own blob container so the site
    // (and its archive) no longer depends on NASA's image URLs staying the same.
    public static class ImageArchiver
    {
        public const string DefaultContainer = "images";
        private const long MaxImageBytes = 25 * 1024 * 1024; // guard against unexpectedly huge files

        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TodayInSpace/1.0 (+https://github.com/Jffrsntaylor/TodayInSpace)");
            return client;
        }

        public static BlobContainerClient GetContainer(string connStr)
        {
            string name = Environment.GetEnvironmentVariable("Storage__ImagesContainerName") ?? DefaultContainer;
            return new BlobContainerClient(connStr, name);
        }

        // Returns the blob name on success (including when the image was already archived),
        // or null if there was nothing to archive or the download failed.
        public static async Task<string?> ArchiveAsync(
            BlobContainerClient images, string date, string? imageUrl, ILogger log)
        {
            string? blobName = ApodImageNaming.GetBlobName(date, imageUrl);
            if (blobName == null)
                return null;

            try
            {
                await images.CreateIfNotExistsAsync();
                var blob = images.GetBlobClient(blobName);

                if (await blob.ExistsAsync())
                    return blobName;

                using var response = await http.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("Image download for {date} returned {status}.", date, (int)response.StatusCode);
                    return null;
                }

                if (response.Content.Headers.ContentLength > MaxImageBytes)
                {
                    log.LogWarning("Image for {date} is too large to archive ({bytes} bytes).", date, response.Content.Headers.ContentLength);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                await blob.UploadAsync(stream, new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = ApodImageNaming.GetContentType(blobName),
                        CacheControl = "public, max-age=31536000, immutable"
                    }
                });

                log.LogInformation("Archived image for {date} as {blob}.", date, blobName);
                return blobName;
            }
            catch (Exception ex)
            {
                log.LogWarning("Archiving image for {date} failed: {msg}", date, ex.Message);
                return null;
            }
        }
    }
}
