using Azure.Storage.Blobs;
using TodayInSpace.Core;

namespace TodayInSpace.Web.Sky
{
    // Where the launches snapshot lives. An interface so tests don't need real storage.
    public interface ILaunchStore
    {
        // The stored JSON, or null if there isn't one yet (or storage isn't configured).
        Task<string?> ReadAsync();
    }

    // Reads the snapshot the RefreshLaunches function writes to the digests container.
    public class BlobLaunchStore : ILaunchStore
    {
        private readonly string? _connStr;
        private readonly string? _container;

        public BlobLaunchStore(IConfiguration config)
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
                var blob = new BlobContainerClient(_connStr, _container).GetBlobClient(Launches.BlobName);
                return (await blob.DownloadContentAsync()).Value.Content.ToString();
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }
    }
}
