using CursedApp.Models;

namespace CursedApp.Services;

/// <summary>
/// Reads a WoW addon ".toc" file. The format is two parts: "## Key: Value"
/// metadata lines, then a bare list of the .lua/.xml files the addon loads.
/// </summary>
public static class TocParser
{
    /// <summary>
    /// Toc files always separate paths with a backslash, whatever the OS.
    /// Spelled as a code point so the literal survives any tooling in between.
    /// </summary>
    public const char WowPathSeparator = (char)0x5C;

    public static async Task<TocFile?> ParseAsync(string tocPath, CancellationToken ct = default)
    {
        if (!File.Exists(tocPath))
            return null;

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var includes = new List<string>();

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(tocPath, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                var body = line[2..].Trim();
                var separator = body.IndexOf(':');
                if (separator <= 0)
                    continue;

                var key = body[..separator].Trim();
                var value = body[(separator + 1)..].Trim();
                if (key.Length > 0)
                    tags[key] = value;

                continue;
            }

            // A comment that is not metadata.
            if (line.StartsWith('#'))
                continue;

            // Anything else is a file the addon loads. Strip inline comments.
            var commentAt = line.IndexOf('#');
            if (commentAt >= 0)
                line = line[..commentAt].TrimEnd();

            if (line.Length > 0)
                includes.Add(line.Replace(WowPathSeparator, Path.DirectorySeparatorChar));
        }

        return new TocFile
        {
            FilePath = tocPath,
            Tags = tags,
            IncludedFiles = includes,
        };
    }

    /// <summary>
    /// Picks the toc that WoW itself would load for <paramref name="flavor"/>.
    /// Addons ship "Foo.toc", "Foo_Mainline.toc", "Foo_Vanilla.toc" side by side;
    /// the flavor-specific name wins over the plain one.
    /// </summary>
    public static string? FindTocForFlavor(string addonFolder, WowFlavor flavor)
    {
        var folderName = Path.GetFileName(addonFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (var suffix in flavor.TocSuffixes())
        {
            var candidate = Path.Combine(addonFolder, $"{folderName}{suffix}.toc");
            if (File.Exists(candidate))
                return candidate;
        }

        // Some addons name the toc differently from the folder; fall back to any toc.
        try
        {
            return Directory.EnumerateFiles(addonFolder, "*.toc", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
