using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CursedApp.Models;
using CursedApp.Services;

namespace CursedApp.ViewModels;

/// <summary>One row of the "My Addons" table.</summary>
public sealed partial class InstalledAddonViewModel : ObservableObject
{
    private readonly Func<InstalledAddonViewModel, CancellationToken, Task> _updateAction;
    private readonly Action<string> _openUrl;

    public InstalledAddonViewModel(
        InstalledAddon addon,
        Func<InstalledAddonViewModel, CancellationToken, Task> updateAction,
        Action<string> openUrl)
    {
        Addon = addon;
        _updateAction = updateAction;
        _openUrl = openUrl;

        Status = addon.Status;
        StatusDetail = addon.StatusDetail;
        InstalledVersion = addon.InstalledVersion;
    }

    public InstalledAddon Addon { get; private set; }

    public string Name => Addon.Name;

    public string? Author => Addon.Author;

    public string? WebsiteUrl => Addon.WebsiteUrl;

    public bool HasWebsite => !string.IsNullOrWhiteSpace(Addon.WebsiteUrl);

    public string FolderSummary => Addon.Components.Count == 1
        ? Addon.Components[0].Folder
        : $"{Addon.Components.Count} folders";

    public string FolderTooltip => string.Join(Environment.NewLine, Addon.Folders);

    /// <summary>Folders this addon installs; shown when a bundle row is expanded.</summary>
    public IReadOnlyList<AddonComponent> Components => Addon.Components;

    /// <summary>True for a multi-folder addon, which gets an expander.</summary>
    public bool IsBundle => Addon.IsBundle;

    /// <summary>
    /// Whether the nested folder list is showing. Bundle contents are purely
    /// informational — they carry no status and no actions of their own, because
    /// the addon is installed and updated as a whole.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsComponents))]
    public partial bool IsExpanded { get; set; }

    /// <summary>
    /// Guards the nested list on both conditions. A single-folder addon has no
    /// expander at all, so listing it as its own child would be noise.
    /// </summary>
    public bool ShowsComponents => IsBundle && IsExpanded;

    /// <summary>
    /// Flips the nested list. A command rather than a two-way IsChecked binding:
    /// inside a DataGrid cell template the write-back never reached the view
    /// model, so the chevron's own visual state changed while nothing else did.
    /// </summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public string? ProviderName => Addon.ProviderName;

    [ObservableProperty]
    public partial string InstalledVersion { get; set; }

    /// <summary>
    /// The client version the installed files target, e.g. "12.1.0". Shown in
    /// place of the addon's own version string, which differs wildly between
    /// authors and says nothing about compatibility.
    /// </summary>
    public string GameVersionText => Addon.GameVersion ?? "—";

    /// <summary>The addon's own version, kept for the row tooltip.</summary>
    public string VersionTooltip => string.IsNullOrWhiteSpace(InstalledVersion) || InstalledVersion == "—"
        ? "The addon does not declare a version"
        : $"Addon version: {InstalledVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(CanUpdateOnSite))]
    [NotifyPropertyChangedFor(nameof(ShowsStatusText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial AddonStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? StatusDetail { get; set; }

    /// <summary>Set while an update is downloading; drives the row's progress bar.</summary>
    [ObservableProperty]
    public partial double? Progress { get; set; }

    [ObservableProperty]
    public partial string? ProgressStage { get; set; }

    public string? AvailableVersion => Addon.LatestRelease?.Version;

    /// <summary>An update exists and this app is allowed to fetch it.</summary>
    public bool CanUpdate => Status == AddonStatus.UpdateAvailable && Addon.LatestRelease?.IsDownloadable == true;

    /// <summary>
    /// An update exists but the author switched off third-party distribution, so
    /// the only honest action is to send the user to the addon's own page.
    /// </summary>
    public bool CanUpdateOnSite =>
        Status == AddonStatus.UpdateAvailable
        && Addon.LatestRelease?.IsDownloadable != true
        && HasWebsite;

    /// <summary>True when the Status cell shows plain text rather than a button.</summary>
    public bool ShowsStatusText => !CanUpdate && !CanUpdateOnSite;

    public bool IsBusy => Status == AddonStatus.Installing;

    public string UpdateTooltip => AvailableVersion is { Length: > 0 } version
        ? $"Update to {version}"
        : "Update";

    public string OnSiteTooltip => AvailableVersion is { Length: > 0 } version
        ? $"{version} is available, but the author does not allow downloads through the API. Opens the addon page."
        : "Opens the addon page.";

    /// <summary>The text shown in the Status column when there is no button.</summary>
    public string StatusText => Status switch
    {
        AddonStatus.UpToDate => "Up to date",
        AddonStatus.UpdateAvailable => "Update available",
        AddonStatus.Installing => ProgressStage ?? "Working…",
        AddonStatus.Failed => StatusDetail ?? "Failed",
        _ => StatusDetail ?? "Unknown",
    };

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateAsync(CancellationToken ct) => _updateAction(this, ct);

    [RelayCommand]
    private void OpenWebsite()
    {
        if (Addon.WebsiteUrl is { Length: > 0 } url)
            _openUrl(url);
    }

    /// <summary>Replaces the underlying model after an install, keeping the row in place.</summary>
    public void Apply(InstalledAddon updated)
    {
        Addon = updated;
        InstalledVersion = updated.InstalledVersion;
        StatusDetail = updated.StatusDetail;
        Status = updated.Status;

        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(FolderSummary));
        OnPropertyChanged(nameof(FolderTooltip));
        OnPropertyChanged(nameof(Components));
        OnPropertyChanged(nameof(GameVersionText));
        OnPropertyChanged(nameof(VersionTooltip));
        OnPropertyChanged(nameof(IsBundle));
    }

    public void ReportProgress(InstallProgress progress)
    {
        ProgressStage = progress.Stage;
        Progress = progress.Fraction;
        OnPropertyChanged(nameof(StatusText));
    }
}
