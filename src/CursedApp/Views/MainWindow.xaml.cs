using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CursedApp.Services;
using CursedApp.ViewModels;

namespace CursedApp.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settings;

    public MainWindow(MainViewModel viewModel, SettingsService settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();
        DataContext = viewModel;

        RestoreWindowPlacement();

        // The native caption is light by default, which reads as a bright band
        // above a dark app. Paint it from the same palette the window uses.
        DarkTitleBar.Apply(
            this,
            caption: ColorFrom("BackgroundColor", Color.FromRgb(0x14, 0x16, 0x1A)),
            text: ColorFrom("TextColor", Color.FromRgb(0xE9, 0xEC, 0xF1)),
            border: ColorFrom("BorderColor", Color.FromRgb(0x31, 0x35, 0x3E)));
    }

    private static Color ColorFrom(string resourceKey, Color fallback) =>
        Application.Current?.TryFindResource(resourceKey) is Color color ? color : fallback;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DataGridColumnPersistence.Attach(
            InstalledGrid, "installed", _settings.Current.ColumnWidths, _settings.Save);

        DataGridColumnPersistence.Attach(
            SearchGrid, "search", _settings.Current.ColumnWidths, _settings.Save);

        SearchBox.Focus();

        await _viewModel.InitializeAsync();
    }

    /// <summary>Ctrl+F puts the caret in the search box.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void RestoreWindowPlacement()
    {
        var saved = _settings.Current;

        // Guard against a size saved on a monitor that is no longer attached.
        var maxWidth = SystemParameters.VirtualScreenWidth;
        var maxHeight = SystemParameters.VirtualScreenHeight;

        if (saved.WindowWidth >= MinWidth && saved.WindowWidth <= maxWidth)
            Width = saved.WindowWidth;

        if (saved.WindowHeight >= MinHeight && saved.WindowHeight <= maxHeight)
            Height = saved.WindowHeight;

        if (saved.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var current = _settings.Current;
        current.WindowMaximized = WindowState == WindowState.Maximized;

        // RestoreBounds holds the un-maximised size, which is what to reopen at.
        if (WindowState == WindowState.Normal)
        {
            current.WindowWidth = Width;
            current.WindowHeight = Height;
        }
        else if (!RestoreBounds.IsEmpty)
        {
            current.WindowWidth = RestoreBounds.Width;
            current.WindowHeight = RestoreBounds.Height;
        }

        _settings.Save();

        base.OnClosing(e);
    }
}
