namespace CursedApp.Models;

/// <summary>
/// One playable WoW client found under the user-selected WoW root folder.
/// </summary>
public sealed record WowInstallation(string RootPath, string FlavorFolder, WowFlavor Flavor)
{
    public string ClientPath => Path.Combine(RootPath, FlavorFolder);

    public string AddonsPath => Path.Combine(ClientPath, "Interface", "AddOns");

    public string DisplayName => Flavor.ToDisplayName();

    public bool Exists => Directory.Exists(AddonsPath);

    /// <summary>
    /// A record's generated ToString prints every member, which is what any
    /// control falls back to when no item template applies. Keep it short.
    /// </summary>
    public override string ToString() => DisplayName;
}
