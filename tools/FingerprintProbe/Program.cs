using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CursedApp.Models;
using CursedApp.Services;

namespace CursedApp.Tools.FingerprintProbe;

/// <summary>
/// Works out which set of files CurseForge actually hashes when it fingerprints
/// an addon folder.
///
/// The trick is that /v1/fingerprints is itself the oracle: send one batch per
/// candidate algorithm and count how many folders each batch identifies. No
/// guessing, no reverse engineering — whichever variant matches the most local
/// folders is the one CurseForge uses.
///
/// Reads the WoW path and API key from the app's own settings.json. The key is
/// never printed.
/// </summary>
internal static class Program
{
    private const int WowGameId = 1;

    private static async Task<int> Main(string[] args)
    {
        // --solve <folderName> <expectedFingerprint>
        if (args is ["--solve", var solveFolder, var expectedText] && long.TryParse(expectedText, out var expected))
        {
            var solveSettings = await LoadSettingsAsync();
            if (solveSettings is null)
                return 1;

            var install = WowLocator.FindInstallations(solveSettings.WowRootPath)
                .FirstOrDefault(i => string.Equals(i.FlavorFolder, solveSettings.SelectedFlavorFolder, StringComparison.OrdinalIgnoreCase))
                ?? WowLocator.FindInstallations(solveSettings.WowRootPath).FirstOrDefault();

            if (install is null)
            {
                Console.Error.WriteLine("No WoW client found.");
                return 1;
            }

            return await Solver.RunAsync(install.AddonsPath, solveFolder, expected);
        }

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CursedApp",
            "settings.json");

        if (!File.Exists(settingsPath))
        {
            Console.Error.WriteLine($"No settings at {settingsPath}. Run the app and set the folder + API key first.");
            return 1;
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(settingsPath));
        if (settings?.CurseForgeApiKey is not { Length: > 0 } apiKey)
        {
            Console.Error.WriteLine("settings.json has no CurseForge API key.");
            return 1;
        }

        var installation = WowLocator.FindInstallations(settings.WowRootPath)
            .FirstOrDefault(i => string.Equals(i.FlavorFolder, settings.SelectedFlavorFolder, StringComparison.OrdinalIgnoreCase))
            ?? WowLocator.FindInstallations(settings.WowRootPath).FirstOrDefault();

        if (installation is null)
        {
            Console.Error.WriteLine($"No WoW client found under '{settings.WowRootPath}'.");
            return 1;
        }

        Console.WriteLine($"Addons: {installation.AddonsPath}");
        Console.WriteLine($"Flavor: {installation.DisplayName}");
        Console.WriteLine();

        var folders = Directory.EnumerateDirectories(installation.AddonsPath)
            .Where(p => !Path.GetFileName(p).StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase))
            .Where(p => Directory.EnumerateFiles(p, "*.toc").Any())
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"{folders.Count} addon folders with a toc.");
        Console.WriteLine();

        var variants = BuildVariants();

        using var http = new HttpClient { BaseAddress = new Uri("https://api.curseforge.com") };
        http.DefaultRequestHeaders.Add("x-api-key", apiKey.Trim());

        var results = new List<(string Name, int Matched, Dictionary<string, long> ByFolder, HashSet<long> Matches)>();

        foreach (var (name, describe, collect) in variants)
        {
            var byFolder = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                try
                {
                    byFolder[Path.GetFileName(folder)] = await ComputeAsync(folder, installation.Flavor, collect);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ! {Path.GetFileName(folder)}: {ex.Message}");
                }
            }

            var matched = await QueryMatchesAsync(http, [.. byFolder.Values]);

            // Count local folders that were identified, not fingerprints returned:
            // a matched file also reports modules for folders that are not installed.
            var identified = byFolder.Values.Count(matched.Contains);
            results.Add((name, identified, byFolder, matched));

            Console.WriteLine($"{name,-46} {identified,3}/{folders.Count}   {describe}");
        }

        Console.WriteLine();

        var best = results.OrderByDescending(r => r.Matched).First();
        Console.WriteLine($"Best variant: {best.Name} ({best.Matched}/{folders.Count})");
        Console.WriteLine();

        Console.WriteLine("Folders the best variant still does not identify:");
        foreach (var (folderName, fingerprint) in best.ByFolder.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!best.Matches.Contains(fingerprint))
                Console.WriteLine($"  {folderName}");
        }

        // Second pass: for the named searches, pull the expected fingerprints
        // straight out of the catalogue and hold them against what each variant
        // computes locally. This distinguishes "our algorithm is wrong" from
        // "these files are not a CurseForge release at all".
        foreach (var term in args)
        {
            Console.WriteLine();
            Console.WriteLine($"=== expected fingerprints for search '{term}' ===");
            await CompareAgainstCatalogueAsync(http, term, installation, folders, variants, results);
        }

        return 0;
    }

    private static async Task CompareAgainstCatalogueAsync(
        HttpClient http,
        string searchTerm,
        WowInstallation installation,
        List<string> folders,
        List<(string Name, string Describe, Func<string, TocFile, IEnumerable<string>> Collect)> variants,
        List<(string Name, int Matched, Dictionary<string, long> ByFolder, HashSet<long> Matches)> results)
    {
        var typeId = installation.Flavor.CurseForgeGameVersionTypeId();
        var url = $"/v1/mods/search?gameId={WowGameId}&searchFilter={Uri.EscapeDataString(searchTerm)}&pageSize=5"
            + (typeId is null ? string.Empty : $"&gameVersionTypeId={typeId}");

        using var searchResponse = await http.GetAsync(url);
        if (!searchResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"  search failed: {(int)searchResponse.StatusCode}");
            return;
        }

        using var searchDoc = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
        if (!searchDoc.RootElement.TryGetProperty("data", out var mods))
            return;

        foreach (var mod in mods.EnumerateArray())
        {
            var modId = mod.GetProperty("id").GetInt32();
            var modName = mod.TryGetProperty("name", out var n) ? n.GetString() : $"#{modId}";
            Console.WriteLine($"  mod {modId} \"{modName}\"");

            using var filesResponse = await http.GetAsync(
                $"/v1/mods/{modId}/files?pageSize=6" + (typeId is null ? string.Empty : $"&gameVersionTypeId={typeId}"));

            if (!filesResponse.IsSuccessStatusCode)
                continue;

            using var filesDoc = JsonDocument.Parse(await filesResponse.Content.ReadAsStringAsync());
            if (!filesDoc.RootElement.TryGetProperty("data", out var files))
                continue;

            foreach (var file in files.EnumerateArray())
            {
                var display = file.TryGetProperty("displayName", out var d) ? d.GetString() : "?";
                if (!file.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
                    continue;

                Console.WriteLine($"    file \"{display}\"");

                foreach (var module in modules.EnumerateArray())
                {
                    var moduleName = module.TryGetProperty("name", out var m) ? m.GetString() : null;
                    if (moduleName is null)
                        continue;

                    var expected = module.GetProperty("fingerprint").GetInt64();

                    var localFolder = folders.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), moduleName, StringComparison.OrdinalIgnoreCase));

                    if (localFolder is null)
                        continue;

                    Console.WriteLine($"      {moduleName,-34} expected {expected}");

                    foreach (var (variantName, _, collect) in variants)
                    {
                        var computed = await ComputeAsync(localFolder, installation.Flavor, collect);
                        var mark = computed == expected ? "  <== MATCH" : string.Empty;
                        Console.WriteLine($"        {variantName,-34} {computed}{mark}");
                    }
                }
            }
        }
    }

    private static async Task<AppSettings?> LoadSettingsAsync()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CursedApp",
            "settings.json");

        if (File.Exists(path))
            return JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path));

        Console.Error.WriteLine($"No settings at {path}.");
        return null;
    }

    /// <summary>Each variant is one theory about which files get hashed.</summary>
    private static List<(string Name, string Describe, Func<string, TocFile, IEnumerable<string>> Collect)> BuildVariants() =>
    [
        ("toc-only",
         "just the .toc",
         static (folder, toc) => [toc.FilePath]),

        ("toc + listed files",
         "toc plus the files the toc lists",
         static (folder, toc) => Prepend(toc.FilePath, TocListed(folder, toc))),

        ("toc + listed + xml refs",
         "what the app does today",
         static (folder, toc) => Prepend(toc.FilePath, TocListedWithXml(folder, toc))),

        ("all files, recursive",
         "every file under the folder",
         static (folder, toc) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)),

        ("all files, recursive, code+media",
         "recursive, filtered to the extensions CurseForge tracks",
         static (folder, toc) => Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(static f => TrackedExtensions.Contains(Path.GetExtension(f)))),

        ("all tocs + listed + xml refs",
         "as today, but seeded from every toc in the folder",
         static (folder, toc) =>
         {
             var files = new List<string>();
             foreach (var tocPath in Directory.EnumerateFiles(folder, "*.toc", SearchOption.TopDirectoryOnly))
             {
                 files.Add(tocPath);
                 var parsed = TocParser.ParseAsync(tocPath).GetAwaiter().GetResult();
                 if (parsed is not null)
                     files.AddRange(TocListedWithXml(folder, parsed));
             }

             return files;
         }),
    ];

    private static readonly HashSet<string> TrackedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".toc", ".lua", ".xml", ".tga", ".blp", ".ttf", ".otf", ".mp3", ".ogg", ".wav", ".png", ".jpg", ".txt",
    };

    private static IEnumerable<string> Prepend(string first, IEnumerable<string> rest)
    {
        yield return first;
        foreach (var item in rest)
            yield return item;
    }

    private static IEnumerable<string> TocListed(string folder, TocFile toc)
    {
        foreach (var relative in toc.IncludedFiles)
        {
            var resolved = Resolve(folder, folder, relative);
            if (resolved is not null && File.Exists(resolved))
                yield return resolved;
        }
    }

    private static IEnumerable<string> TocListedWithXml(string folder, TocFile toc)
    {
        var pending = new Queue<string>();
        foreach (var relative in toc.IncludedFiles)
        {
            if (Resolve(folder, folder, relative) is { } resolved)
                pending.Enqueue(resolved);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var path = pending.Dequeue();
            if (!File.Exists(path) || !seen.Add(path))
                continue;

            yield return path;

            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;

            var directory = Path.GetDirectoryName(path)!;
            foreach (var reference in XmlReferences(path))
            {
                if (Resolve(folder, directory, reference) is { } resolved)
                    pending.Enqueue(resolved);
            }
        }
    }

    private static IEnumerable<string> XmlReferences(string xmlPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(xmlPath);
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var match in System.Text.RegularExpressions.Regex
            .Matches(text, @"file\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            yield return ((System.Text.RegularExpressions.Match)match)
                .Groups[1].Value.Replace((char)0x5C, Path.DirectorySeparatorChar);
        }
    }

    private static string? Resolve(string root, string baseDirectory, string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative))
            return null;

        try
        {
            var combined = Path.GetFullPath(Path.Combine(baseDirectory, relative));
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? combined : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hashes each file with normalized murmur2, sorts the hashes, then hashes
    /// their concatenated decimal text. Only the file set varies per variant.
    /// </summary>
    private static async Task<long> ComputeAsync(
        string folder,
        WowFlavor flavor,
        Func<string, TocFile, IEnumerable<string>> collect)
    {
        var tocPath = TocParser.FindTocForFlavor(folder, flavor);
        if (tocPath is null)
            return 0;

        var toc = await TocParser.ParseAsync(tocPath);
        if (toc is null)
            return 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new List<uint>();

        foreach (var file in collect(folder, toc))
        {
            var full = Path.GetFullPath(file);
            if (!seen.Add(full) || !File.Exists(full))
                continue;

            hashes.Add(Murmur2.HashNormalized(await File.ReadAllBytesAsync(full)));
        }

        hashes.Sort();

        var text = new StringBuilder(hashes.Count * 10);
        foreach (var hash in hashes)
            text.Append(hash.ToString(CultureInfo.InvariantCulture));

        return Murmur2.Hash(Encoding.ASCII.GetBytes(text.ToString()));
    }

    private static async Task<HashSet<long>> QueryMatchesAsync(HttpClient http, List<long> fingerprints)
    {
        var matched = new HashSet<long>();

        foreach (var batch in fingerprints.Where(f => f != 0).Distinct().Chunk(500))
        {
            using var response = await http.PostAsJsonAsync(
                $"/v1/fingerprints/{WowGameId}",
                new { fingerprints = batch });

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"  ! API {(int)response.StatusCode} {response.ReasonPhrase}");
                continue;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("data", out var data))
                continue;

            // "Exact" means every module of a file matched, so a single folder of
            // a multi-folder addon lands in partialMatches. Both identify the
            // folder, so both count.
            foreach (var bucket in new[] { "exactMatches", "partialMatches" })
            {
                if (!data.TryGetProperty(bucket, out var list) || list.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var match in list.EnumerateArray())
                {
                    if (!match.TryGetProperty("file", out var file)
                        || !file.TryGetProperty("modules", out var modules)
                        || modules.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var module in modules.EnumerateArray())
                    {
                        if (module.TryGetProperty("fingerprint", out var fp))
                            matched.Add(fp.GetInt64());
                    }
                }
            }
        }

        return matched;
    }
}
