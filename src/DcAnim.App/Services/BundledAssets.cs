using System.Reflection;

namespace DcAnim.App.Services;

/// <summary>
/// Makes the bundled default-outfit atlas available to the loader. In a
/// folder/publish build the files sit next to the executable; in a
/// single-file build they are embedded resources, extracted once into the
/// user's local app-data cache so the normal file loader can read them.
/// Assets are © Motion Twin / Evil Empire, included with permission (README).
/// </summary>
public static class BundledAssets
{
    public const string Version = "v1";
    private static readonly string[] Files = ["beheaded.atlas", "beheaded0.png", "beheaded1.png", "beheaded2.png"];

    /// <summary>Returns a directory containing the atlas set, or null if unavailable.</summary>
    public static string? EnsureAvailable()
    {
        string? loose = LooseDirectory(AppContext.BaseDirectory);
        if (loose is not null)
            return loose;

        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeadCellsAnimationStudio", "bundled", Version);
        if (Files.All(f => File.Exists(Path.Combine(cache, f))))
            return cache;

        try
        {
            Directory.CreateDirectory(cache);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (string name in Files)
            {
                using var stream = assembly.GetManifestResourceStream("Bundled." + name);
                if (stream is null)
                    return null; // embedded payload absent (e.g. plain debug build)
                string dest = Path.Combine(cache, name);
                using (var tmp = new FileStream(dest + ".tmp", FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(tmp);
                    tmp.Flush();
                }
                File.Move(dest + ".tmp", dest, overwrite: true);
            }
            return cache;
        }
        catch
        {
            return null; // never block startup on cache/extraction problems
        }
    }

    private static string? LooseDirectory(string baseDir)
    {
        string candidate = Path.Combine(baseDir, "bundled", "beheaded");
        if (File.Exists(Path.Combine(candidate, "beheaded.atlas")))
            return candidate;
        return null;
    }
}
