using CursedApp.Models;

namespace CursedApp.Services.Providers;

/// <summary>
/// A source of addon metadata. CurseForge is the implementation shipped here;
/// the interface keeps room for WoWInterface or Wago without touching the UI.
/// </summary>
public interface IAddonProvider
{
    string Name { get; }

    /// <summary>False when the provider is not usable yet, e.g. no API key entered.</summary>
    bool IsConfigured { get; }

    /// <summary>Explains to the user why <see cref="IsConfigured"/> is false.</summary>
    string? ConfigurationHint { get; }

    /// <summary>
    /// Identifies scanned folders and groups them into logical addons, resolving
    /// the installed version and whether a newer release exists.
    /// </summary>
    Task<IReadOnlyList<InstalledAddon>> MatchInstalledAsync(
        IReadOnlyList<AddonFolder> folders,
        WowFlavor flavor,
        CancellationToken ct = default);

    Task<IReadOnlyList<AddonSearchResult>> SearchAsync(
        string query,
        WowFlavor flavor,
        CancellationToken ct = default);

    /// <summary>Resolves the newest release for an addon, for install or update.</summary>
    Task<AddonRelease?> GetLatestReleaseAsync(
        string providerId,
        WowFlavor flavor,
        CancellationToken ct = default);
}
