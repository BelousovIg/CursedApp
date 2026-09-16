using System.Text;
using System.Text.RegularExpressions;

namespace CursedApp.Models;

/// <summary>
/// Addon titles routinely embed WoW UI escape sequences: "|cff33ff99Details|r",
/// "|TInterface\Icons\foo:16|t". They must not reach the UI verbatim.
/// </summary>
public static partial class TocStripper
{
    // A colour code is "|c" plus exactly eight hex digits (AARRGGBB). The count
    // must not be a range: with "|cFFFFAA00Details!", a 6-8 range happily eats
    // "FFFFAA00De" — both of those trailing letters are valid hex — and the
    // title comes out as "tails!".
    [GeneratedRegex(@"\|c[0-9a-fA-F]{8}|\|r", RegexOptions.CultureInvariant)]
    private static partial Regex ColorCodeRegex { get; }

    [GeneratedRegex(@"\|T.*?\|t", RegexOptions.CultureInvariant)]
    private static partial Regex TextureRegex { get; }

    public static string? StripColorCodes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var stripped = TextureRegex.Replace(value, string.Empty);
        stripped = ColorCodeRegex.Replace(stripped, string.Empty);
        return stripped.Trim();
    }

    /// <summary>
    /// Normalizes a version string for comparison: drops a leading "v",
    /// trims whitespace and collapses case.
    /// </summary>
    public static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        var span = version.AsSpan().Trim();
        if (span.Length > 1 && (span[0] is 'v' or 'V') && !char.IsLetter(span[1]))
            span = span[1..];

        var builder = new StringBuilder(span.Length);
        foreach (var ch in span)
        {
            if (!char.IsWhiteSpace(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
