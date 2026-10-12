using Azure.Storage.Blobs;
using Microsoft.Extensions.Caching.Memory;
using TodayInSpace.Core;

namespace TodayInSpace.Web.Sky
{
    // Where the active satellites' TLEs live. An interface so tests don't need real storage.
    public interface IActiveSatelliteStore
    {
        // The stored JSON, or null if there isn't one yet (or storage isn't configured).
        Task<string?> ReadAsync();
    }

    // Reads the file the RefreshActiveSatellites function writes to the digests container.
    public class BlobActiveSatelliteStore : IActiveSatelliteStore
    {
        private readonly string? _connStr;
        private readonly string? _container;

        public BlobActiveSatelliteStore(IConfiguration config)
        {
            _connStr = config["Storage:ConnectionString"];
            _container = config["Storage:ContainerName"];
        }

        public async Task<string?> ReadAsync()
        {
            if (string.IsNullOrEmpty(_connStr) || string.IsNullOrEmpty(_container))
                return null;

            try
            {
                var blob = new BlobContainerClient(_connStr, _container).GetBlobClient(ActiveSatellites.BlobName);
                return (await blob.DownloadContentAsync()).Value.Content.ToString();
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }
    }

    // Serves the stored JSON as-is: it's ~2.7 MB, so it isn't parsed per request, only held in memory
    // so a burst of visitors turning on Starlink doesn't mean a blob read each.
    public class ActiveSatellitesService
    {
        private const string Key = "sky:active-satellites";
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);   // the function refreshes every 6 hours
        private static readonly TimeSpan StaleTtl = TimeSpan.FromDays(3);  // TLEs stay good enough for a few days
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);

        private readonly IActiveSatelliteStore _store;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ActiveSatellitesService> _logger;

        public ActiveSatellitesService(IActiveSatelliteStore store, IMemoryCache cache, ILogger<ActiveSatellitesService> logger)
        {
            _store = store;
            _cache = cache;
            _logger = logger;
        }

        // Null until the function has written the file once (and storage can be read).
        public async Task<string?> GetJsonAsync()
        {
            if (_cache.TryGetValue(Key, out string? json) && json != null)
                return json;

            await RefreshLock.WaitAsync();
            try
            {
                if (_cache.TryGetValue(Key, out json) && json != null)
                    return json;

                json = await _store.ReadAsync();
                // A cheap shape check; the function only ever writes a JSON object.
                if (json != null && json.TrimStart().StartsWith('{'))
                {
                    _cache.Set(Key, json, Ttl);
                    _cache.Set(Key + ":stale", json, StaleTtl);
                    return json;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("active-tles.json read failed: {msg}", ex.Message);
            }
            finally
            {
                RefreshLock.Release();
            }

            return _cache.TryGetValue(Key + ":stale", out string? stale) ? stale : null;
        }
    }
}
