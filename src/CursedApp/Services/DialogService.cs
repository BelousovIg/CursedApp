using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace CursedApp.Services;

/// <summary>WPF implementation of the view models' window interactions.</summary>
public sealed class DialogService(ILogSink log) : IDialogService
{
    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        // The shell resolves InitialDirectory through IShellItem, which rejects
        // anything that is not a canonical Windows path — forward slashes
        // included. A bad remembered path must not stop the user from picking
        // a new one, so fall back to the default location instead of failing.
        if (NormalizeDirectory(initialDirectory) is { } startFolder)
            dialog.InitialDirectory = startFolder;

        try
        {
            return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
        }
        catch (ArgumentException ex) when (dialog.InitialDirectory.Length > 0)
        {
            log.Warn($"Folder dialog rejected the initial directory '{dialog.InitialDirectory}': {ex.Message}");

            dialog.InitialDirectory = string.Empty;
            return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
        }
    }

    public string? PickFile(string title, string filter, string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = false,
            CheckFileExists = true,
        };

        if (NormalizeDirectory(initialDirectory) is { } startFolder)
            dialog.InitialDirectory = startFolder;

        try
        {
            return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
        }
        catch (ArgumentException ex) when (dialog.InitialDirectory.Length > 0)
        {
            log.Warn($"File dialog rejected the initial directory: {ex.Message}");

            dialog.InitialDirectory = string.Empty;
            return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
        }
    }

    /// <summary>
    /// Returns an existing directory as a canonical Windows path, or null when
    /// the input is unusable.
    /// </summary>
    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var full = Path.GetFullPath(path);
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow!,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow!,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void OpenUrl(string url)
    {
        // Also used for local folders, which Explorer opens the same way.
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn($"Could not open '{url}': {ex.Message}");
        }
    }
}
