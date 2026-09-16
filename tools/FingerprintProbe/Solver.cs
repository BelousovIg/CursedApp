using System.Globalization;
using System.Text;
using CursedApp.Models;
using CursedApp.Services;

namespace CursedApp.Tools.FingerprintProbe;

/// <summary>
/// Brute-forces the fingerprint recipe against a known-good value.
///
/// The folder fingerprint is built in stages — pick the files, hash each one,
/// order the hashes, serialize them, hash that — and each stage has a couple of
/// plausible choices. With one confirmed target from the catalogue, trying every
/// combination settles the recipe outright instead of guessing at it.
/// </summary>
internal static class Solver
{
    public static async Task<int> RunAsync(string addonsPath, string folderName, long expected)
    {
        var folder = Path.Combine(addonsPath, folderName);
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"No such folder: {folder}");
            return 1;
        }

        Console.WriteLine($"Folder:   {folder}");
        Console.WriteLine($"Expected: {expected}");
        Console.WriteLine();

        var tocPath = TocParser.FindTocForFlavor(folder, WowFlavor.Retail);
        if (tocPath is null)
        {
            Console.Error.WriteLine("No toc found.");
            return 1;
        }

        var toc = await TocParser.ParseAsync(tocPath);
        if (toc is null)
            return 1;

        // ---- stage 1: which files -------------------------------------------

        var listedFiles = new List<string>();
        foreach (var relative in toc.IncludedFiles)
        {
            var full = Path.GetFullPath(Path.Combine(folder, relative));
            if (File.Exists(full))
                listedFiles.Add(full);
        }

        var allFiles = Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allTocs = Directory
            .EnumerateFiles(folder, "*.toc", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Files the tocs pull in, following XML includes — what the app computes.
        var reachable = new List<string>(allTocs);
        var pending = new Queue<string>();
        var seenReachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in allTocs)
        {
            if (await TocParser.ParseAsync(t) is not { } parsed)
                continue;

            foreach (var rel in parsed.IncludedFiles)
                pending.Enqueue(Path.GetFullPath(Path.Combine(folder, rel)));
        }

        while (pending.Count > 0)
        {
            var file = pending.Dequeue();
            if (!File.Exists(file) || !seenReachable.Add(file))
                continue;

            reachable.Add(file);

            if (!file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;

            var dir = Path.GetDirectoryName(file)!;
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(
                await File.ReadAllTextAsync(file),
                @"file\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var reference = ((System.Text.RegularExpressions.Match)m).Groups[1].Value
                    .Replace((char)0x5C, Path.DirectorySeparatorChar);
                pending.Enqueue(Path.GetFullPath(Path.Combine(dir, reference)));
            }
        }

        static bool HasExtension(string file, params string[] extensions) =>
            extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

        var mediaExtensions = new[] { ".ogg", ".mp3", ".wav", ".jpg", ".jpeg", ".png", ".tga", ".blp", ".ttf", ".otf" };

        var fileSets = new List<(string Name, List<string> Files)>
        {
            ("toc + listed", [tocPath, .. listedFiles]),
            ("listed only (no toc)", [.. listedFiles]),
            ("all files", allFiles),
            ("all files minus tocs", [.. allFiles.Where(f => !HasExtension(f, ".toc"))]),
            ("all tocs + listed", [.. allTocs, .. listedFiles]),
            ("all tocs + reachable (xml refs)", reachable),
            ("all code (toc+lua+xml)", [.. allFiles.Where(f => HasExtension(f, ".toc", ".lua", ".xml"))]),
            ("all code + txt", [.. allFiles.Where(f => HasExtension(f, ".toc", ".lua", ".xml", ".txt"))]),
            ("all non-media", [.. allFiles.Where(f => !HasExtension(f, mediaExtensions))]),
            ("top directory only", [.. allFiles.Where(f => string.Equals(Path.GetDirectoryName(f), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))]),
            ("all files minus .md", [.. allFiles.Where(f => !HasExtension(f, ".md"))]),
            ("all files minus libs", [.. allFiles.Where(f => !f.Contains(Path.DirectorySeparatorChar + "libs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))]),
            ("reachable + all media", [.. reachable, .. allFiles.Where(f => HasExtension(f, mediaExtensions))]),
        };

        Console.WriteLine("File sets:");
        foreach (var (name, files) in fileSets)
            Console.WriteLine($"  {name,-24} {files.Count} files");
        Console.WriteLine();

        // Cache both hash flavours per file so the search below is cheap.
        var normalized = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);
        var raw = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in fileSets.SelectMany(s => s.Files).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            normalized[file] = [Murmur2.HashNormalized(bytes, 1), Murmur2.HashNormalized(bytes, 0)];
            raw[file] = [Murmur2.Hash(bytes, 1), Murmur2.Hash(bytes, 0)];
        }

        var hits = new List<string>();
        var tried = 0;

        foreach (var (setName, files) in fileSets)
        foreach (var useNormalized in new[] { true, false })
        foreach (var individualSeed in new[] { 1u, 0u })
        foreach (var sortHashes in new[] { true, false })
        foreach (var serializeDecimal in new[] { true, false })
        foreach (var separator in new[] { "", " ", ",", "\n" })
        foreach (var finalSeed in new[] { 1u, 0u })
        foreach (var finalNormalize in new[] { false, true })
        {
            // A separator only exists for the decimal-text serialization.
            if (!serializeDecimal && separator.Length > 0)
                continue;

            tried++;

            var table = useNormalized ? normalized : raw;
            var seedIndex = individualSeed == 1 ? 0 : 1;

            var hashes = new List<uint>(files.Count);
            foreach (var file in files)
            {
                if (table.TryGetValue(file, out var pair))
                    hashes.Add(pair[seedIndex]);
            }

            if (sortHashes)
                hashes.Sort();

            byte[] payload;
            if (serializeDecimal)
            {
                var text = new StringBuilder();
                for (var i = 0; i < hashes.Count; i++)
                {
                    if (i > 0 && separator.Length > 0)
                        text.Append(separator);

                    text.Append(hashes[i].ToString(CultureInfo.InvariantCulture));
                }

                payload = Encoding.ASCII.GetBytes(text.ToString());
            }
            else
            {
                payload = new byte[hashes.Count * 4];
                for (var i = 0; i < hashes.Count; i++)
                    BitConverter.TryWriteBytes(payload.AsSpan(i * 4, 4), hashes[i]);
            }

            var result = finalNormalize
                ? Murmur2.HashNormalized(payload, finalSeed)
                : Murmur2.Hash(payload, finalSeed);

            if (result == expected)
            {
                hits.Add(
                    $"  files={setName}; individual={(useNormalized ? "normalized" : "raw")}, seed={individualSeed}; "
                    + $"order={(sortHashes ? "sorted" : "listed")}; "
                    + $"payload={(serializeDecimal ? $"decimal sep='{separator.Replace("\n", "\\n")}'" : "uint32-le")}; "
                    + $"final seed={finalSeed}, normalize={finalNormalize}");
            }
        }

        Console.WriteLine($"Tried {tried} combinations.");
        Console.WriteLine();

        if (hits.Count == 0)
        {
            Console.WriteLine("No combination reproduces the expected value.");
            Console.WriteLine("That points at the file set rather than the arithmetic:");
            Console.WriteLine("the installed files are probably not this release at all.");
            return 2;
        }

        Console.WriteLine($"{hits.Count} combination(s) reproduce it:");
        foreach (var hit in hits)
            Console.WriteLine(hit);

        return 0;
    }
}
