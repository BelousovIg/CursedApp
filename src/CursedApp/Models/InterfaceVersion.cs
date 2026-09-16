using System.Globalization;

namespace CursedApp.Models;

/// <summary>
/// Reads a toc's "## Interface" tag as the client version an addon targets.
///
/// WoW encodes the client version as a single integer: 120100 is 12.1.0,
/// 110002 is 11.0.2, 11507 is 1.15.7. This is the one version number in an
/// addon that means something consistent — an addon's own "## Version" is
/// whatever its author felt like writing.
///
/// A toc commonly lists every client it still supports, oldest first:
/// "## Interface: 120000, 120001, 120005, 120007, 120100". The interesting one
/// is the newest, so these helpers take the maximum rather than the first —
/// reading the first reports an addon built for 12.1 as a 12.0 addon.
/// </summary>
public static class InterfaceVersion
{
    /// <summary>Below this there is no major version to report.</summary>
    private const int MinimumUsable = 10000;

    /// <summary>
    /// The highest interface number the tag lists, or null when there is none.
    /// </summary>
    public static int? ParseHighest(string? interfaceTag)
    {
        if (string.IsNullOrWhiteSpace(interfaceTag))
            return null;

        int? highest = null;

        foreach (var part in interfaceTag.Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;

            if (value >= MinimumUsable && (highest is null || value > highest))
                highest = value;
        }

        return highest;
    }

    /// <summary>Formats an interface number as "major.minor.patch".</summary>
    public static string? Format(int? interfaceNumber)
    {
        if (interfaceNumber is not { } value || value < MinimumUsable)
            return null;

        var major = value / 10000;
        var minor = value / 100 % 100;
        var patch = value % 100;

        return string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{patch}");
    }

    /// <summary>Convenience for going straight from the tag to display text.</summary>
    public static string? ToGameVersion(string? interfaceTag) => Format(ParseHighest(interfaceTag));
}
