using System.Text.RegularExpressions;

namespace TodayInSpace.Web.Helpers
{
    // Flag images for the archive's people card. The home page drops a missing flag in the browser,
    // but the archive is plain Razor, so it only links flags we actually ship.
    public class FlagIcons
    {
        public const string Folder = "lib/flag-icons/4x3";

        private readonly Lazy<IReadOnlySet<string>> _available;

        // The folder is listed once, on first use, and kept for the life of the app.
        public FlagIcons(IWebHostEnvironment env)
        {
            _available = new Lazy<IReadOnlySet<string>>(() => ListCodes(env.WebRootFileProvider.GetDirectoryContents(Folder)
                .Where(f => !f.IsDirectory)
                .Select(f => f.Name)));
        }

        public string? Src(string? code) => Src(code, _available.Value);

        // "/lib/flag-icons/4x3/us.svg" for a two-letter code we have a file for, otherwise null.
        public static string? Src(string? code, IReadOnlySet<string> available)
        {
            if (code == null || !Regex.IsMatch(code, "^[A-Z]{2}$"))
                return null;
            string lower = code.ToLowerInvariant();
            return available.Contains(lower) ? "/" + Folder + "/" + lower + ".svg" : null;
        }

        // File names like "us.svg" -> "us".
        public static IReadOnlySet<string> ListCodes(IEnumerable<string> fileNames) =>
            fileNames
                .Where(n => n.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                .Select(n => n[..^4].ToLowerInvariant())
                .ToHashSet();
    }
}
