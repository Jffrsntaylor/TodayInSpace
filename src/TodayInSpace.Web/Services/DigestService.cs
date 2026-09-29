using System.Text.Json;
using Azure.Storage.Blobs;
using TodayInSpace.Web.Models;

namespace TodayInSpace.Web.Services
{
    public class DigestService
    {
        private readonly string? _connStr;
        private readonly string? _container;

        public DigestService(IConfiguration config)
        {
            _connStr = config["Storage:ConnectionString"];
            _container = config["Storage:ContainerName"];
        }

        // Reads the most recent digest (the timer function keeps "latest.json" updated).
        public async Task<DigestModel?> GetLatestAsync()
        {
            return await ReadDigestAsync("latest.json");
        }

        // Reads the digest for a specific date, e.g. "2026-06-02".
        // Returns null if there's no digest stored for that day.
        public async Task<DigestModel?> GetByDateAsync(string date)
        {
            if (string.IsNullOrWhiteSpace(date))
                return null;

            // Basic safety: only allow YYYY-MM-DD so nobody can request odd blob names.
            if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
                return null;

            return await ReadDigestAsync(date + ".json");
        }

        // Shared helper: download a blob by name and deserialize it.
        private async Task<DigestModel?> ReadDigestAsync(string blobName)
        {
            if (string.IsNullOrEmpty(_connStr) || string.IsNullOrEmpty(_container))
                return null;

            try
            {
                var containerClient = new BlobContainerClient(_connStr, _container);
                var blob = containerClient.GetBlobClient(blobName);

                if (!await blob.ExistsAsync())
                    return null;

                var result = await blob.DownloadContentAsync();
                var json = result.Value.Content.ToString();

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<DigestModel>(json, options);
            }
            catch
            {
                return null;
            }
        }

        // Backwards-compatible: keep the old method name working if anything still calls it.
        public async Task<DigestModel?> GetDigestAsync()
        {
            // Prefer latest.json; fall back to the old single-file name if needed.
            return await GetLatestAsync() ?? await ReadDigestAsync("digests.json");
        }
    }
}