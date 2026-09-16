namespace CursedApp.Services;

/// <summary>
/// Window-level interactions the view models need, behind an interface so the
/// view models stay free of WPF types.
/// </summary>
public interface IDialogService
{
    /// <summary>Returns the picked folder, or null when the user cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory);

    /// <summary>Returns the picked file, or null when the user cancelled.</summary>
    string? PickFile(string title, string filter, string? initialDirectory);

    void ShowError(string title, string message);

    bool Confirm(string title, string message);

    /// <summary>Opens a URL in the user's default browser.</summary>
    void OpenUrl(string url);
}
