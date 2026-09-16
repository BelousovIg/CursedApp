namespace CursedApp.Models;

public enum AddonStatus
{
    /// <summary>The addon could not be matched against the provider's catalog.</summary>
    Unknown,

    /// <summary>Installed version is the latest one available.</summary>
    UpToDate,

    /// <summary>A newer release exists.</summary>
    UpdateAvailable,

    /// <summary>An install or update is running for this addon.</summary>
    Installing,

    /// <summary>The last install or update attempt failed.</summary>
    Failed,
}
