using Microsoft.Win32;

namespace DcAnim.Core.Sources;

/// <summary>
/// Locates a Dead Cells installation without hardcoded user paths:
/// Steam registry keys, then <c>libraryfolders.vdf</c>, then common disk locations.
/// </summary>
public static class GameSourceLocator
{
    public const string ResPakFileName = "res.pak";

    /// <summary>
    /// Default-outfit atlas set shipped with the app (assets © Motion Twin /
    /// Evil Empire, included with permission; see README). Lets the editor run
    /// without a local Steam install.
    /// </summary>
    public static string? FindBundledAtlasDirectory()
    {
        foreach (var root in BundledRoots())
        {
            if (File.Exists(Path.Combine(root, "beheaded.atlas")))
                return root;
        }
        return null;
    }

    private static IEnumerable<string> BundledRoots()
    {
        // Shipped beside the executable and, for `dotnet run`, up the tree.
        string? baseDir = AppContext.BaseDirectory;
        if (baseDir != null)
        {
            yield return Path.Combine(baseDir, "bundled", "beheaded");
            for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
                yield return Path.Combine(dir.FullName, "bundled", "beheaded");
        }
    }

    public static string? FindGameDirectory()
    {
        foreach (var dir in CandidateGameDirectories())
        {
            if (File.Exists(Path.Combine(dir, ResPakFileName)))
                return dir;
        }
        return null;
    }

    public static string? FindResPak() =>
        FindGameDirectory() is { } dir ? Path.Combine(dir, ResPakFileName) : null;

    private static IEnumerable<string> CandidateGameDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> SteamRoots()
        {
            // Registry (Windows). Kept outside try/catch-with-yield by
            // materializing first: yield cannot appear inside a try/catch body.
            var fromRegistry = new List<string>();
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                        using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam")
                            ?? baseKey.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam");
                        var path = key?.GetValue("InstallPath") as string;
                        if (!string.IsNullOrWhiteSpace(path))
                            fromRegistry.Add(path);
                    }
                }
            }
            catch
            {
                // Registry unavailable: fall through to disk probes.
            }
            foreach (var r in fromRegistry)
                yield return r;

            // Common install locations (no usernames).
            foreach (var drive in new[] { "C", "D", "E", "F" })
            {
                yield return $@"{drive}:\Program Files (x86)\Steam";
                yield return $@"{drive}:\Program Files\Steam";
                yield return $@"{drive}:\Steam";
                yield return $@"{drive}:\SteamLibrary";
            }
        }

        foreach (var steamRoot in SteamRoots())
        {
            foreach (var library in LibrariesFrom(steamRoot))
            {
                string dir = Path.Combine(library, "steamapps", "common", "Dead Cells");
                if (seen.Add(dir))
                    yield return dir;
            }
        }
    }

    private static IEnumerable<string> LibrariesFrom(string steamRoot)
    {
        yield return steamRoot;
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;
        // Minimal parse: collect every quoted value that looks like a Windows path.
        foreach (var line in File.ReadLines(vdf))
        {
            var parts = line.Split('"');
            for (int i = 1; i < parts.Length; i += 2)
            {
                string value = parts[i].Replace(@"\\", @"\");
                if (value.Length > 2 && value[1] == ':' && Directory.Exists(value))
                    yield return value;
            }
        }
    }
}
