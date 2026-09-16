namespace CursedApp.Models;

/// <summary>
/// A World of Warcraft client variant. The folder name under the WoW root
/// ("_retail_", "_classic_era_", ...) is what actually identifies it on disk.
/// </summary>
public enum WowFlavor
{
    Unknown = 0,
    Retail,
    Classic,
    ClassicEra,
    RetailPtr,
    ClassicPtr,
    Beta,
}

public static class WowFlavorExtensions
{
    private readonly record struct FlavorInfo(string Folder, WowFlavor Flavor, string Display);

    private static readonly FlavorInfo[] KnownFlavors =
    [
        new("_retail_",      WowFlavor.Retail,     "Retail"),
        new("_classic_",     WowFlavor.Classic,    "Classic"),
        new("_classic_era_", WowFlavor.ClassicEra, "Classic Era"),
        new("_ptr_",         WowFlavor.RetailPtr,  "Retail PTR"),
        new("_classic_ptr_", WowFlavor.ClassicPtr, "Classic PTR"),
        new("_beta_",        WowFlavor.Beta,       "Beta"),
    ];

    public static IReadOnlyList<string> AllFolderNames { get; } = [.. KnownFlavors.Select(f => f.Folder)];

    public static WowFlavor FromFolderName(string folderName)
    {
        foreach (var info in KnownFlavors)
        {
            if (string.Equals(info.Folder, folderName, StringComparison.OrdinalIgnoreCase))
                return info.Flavor;
        }

        return WowFlavor.Unknown;
    }

    public static string ToDisplayName(this WowFlavor flavor)
    {
        foreach (var info in KnownFlavors)
        {
            if (info.Flavor == flavor)
                return info.Display;
        }

        return "Unknown";
    }

    /// <summary>
    /// The ".toc" filename suffixes WoW accepts for this flavor, most specific first.
    /// Addons ship e.g. "Details_Mainline.toc" and "Details_Vanilla.toc" side by side;
    /// the empty suffix is the plain "Details.toc" fallback.
    /// </summary>
    public static IReadOnlyList<string> TocSuffixes(this WowFlavor flavor) => flavor switch
    {
        WowFlavor.Retail or WowFlavor.RetailPtr or WowFlavor.Beta =>
            ["_Mainline", "-Mainline", ""],
        WowFlavor.Classic or WowFlavor.ClassicPtr =>
            ["_Cata", "-Cata", "_Wrath", "-Wrath", "_TBC", "-TBC", ""],
        WowFlavor.ClassicEra =>
            ["_Vanilla", "-Vanilla", "_Classic", "-Classic", ""],
        _ => [""],
    };

    /// <summary>Matches the "gameVersionTypeId" dimension of the CurseForge API.</summary>
    public static int? CurseForgeGameVersionTypeId(this WowFlavor flavor) => flavor switch
    {
        WowFlavor.Retail or WowFlavor.RetailPtr or WowFlavor.Beta => 517,
        WowFlavor.Classic or WowFlavor.ClassicPtr => 73713,
        WowFlavor.ClassicEra => 67408,
        _ => null,
    };
}
