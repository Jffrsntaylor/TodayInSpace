using TodayInSpace.Core.Visibility;

namespace TodayInSpace.Web.Sky
{
    // Loads the Census zip locations once (about 34,000 rows) and keeps them for the life of the app.
    // The file sits outside wwwroot on purpose: it's read here, never served as-is.
    public class ZipLookup
    {
        public static readonly string DataPath = Path.Combine(AppContext.BaseDirectory, "Data", "zcta-2025.csv");

        public ZipLocations Zips { get; }

        public ZipLookup() : this(File.ReadAllText(DataPath)) { }

        public ZipLookup(string csv) => Zips = ZipLocations.Parse(csv);
    }
}
