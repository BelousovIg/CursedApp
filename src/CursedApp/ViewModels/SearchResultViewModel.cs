using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CursedApp.Models;
using CursedApp.Services;

namespace CursedApp.ViewModels;

/// <summary>One row of the "Search" table.</summary>
public sealed partial class SearchResultViewModel : ObservableObject
{
    private readonly Func<SearchResultViewModel, CancellationToken, Task> _installAction;
    private readonly Action<string> _openUrl;

    public SearchResultViewModel(
        AddonSearchResult result,
        bool isInstalled,
        Func<SearchResultViewModel, CancellationToken, Task> installAction,
        Action<string> openUrl)
    {
        Result = result;
        _installAction = installAction;
        _openUrl = openUrl;
        IsInstalled = isInstalled;
    }

    public AddonSearchResult Result { get; }

    public string Name => Result.Name;

    public string? Summary => Result.Summary;

    public string? Author => Result.Author;

    public string? Categories => Result.Categories;

    public string? WebsiteUrl => Result.WebsiteUrl;

    public bool HasWebsite => !string.IsNullOrWhiteSpace(Result.WebsiteUrl);

    public string? Version => Result.LatestRelease?.Version;

    public string DownloadsText => Result.DownloadCount switch
    {
        >= 1_000_000 => (Result.DownloadCount / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 1_000 => (Result.DownloadCount / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "K",
        _ => Result.DownloadCount.ToString(CultureInfo.CurrentCulture),
    };

    public string LastUpdatedText => Result.LastUpdated?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double? Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    public partial string? ProgressStage { get; set; }

    public bool CanInstall => !IsInstalled && !IsBusy && Result.LatestRelease?.IsDownloadable == true;

    public string ActionText => IsBusy
        ? ProgressStage ?? "Working…"
        : IsInstalled
            ? "Installed"
            : Result.LatestRelease is null
                ? "Unavailable"
                : Result.LatestRelease.IsDownloadable
                    ? "Install"
                    : "Site only";

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync(CancellationToken ct) => _installAction(this, ct);

    [RelayCommand]
    private void OpenWebsite()
    {
        if (Result.WebsiteUrl is { Length: > 0 } url)
            _openUrl(url);
    }

    public void ReportProgress(InstallProgress progress)
    {
        ProgressStage = progress.Stage;
        Progress = progress.Fraction;
    }
}
