using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CursedApp.Models;
using CursedApp.Services;
using CursedApp.Services.Providers.CurseForge;

namespace CursedApp.ViewModels;

/// <summary>Tabs in the main window, in display order.</summary>
public enum MainTab
{
    Installed = 0,
    Search = 1,
    Settings = 2,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AddonManager _addons;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly CurseForgeApiClient _api;
    private readonly DownloadWatcher _watcher;
    private readonly ILogSink _log;
    private readonly FileLogSink _fileLog;
    private readonly Dispatcher _dispatcher;

    private bool _isInitialized;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _refreshCts;

    public MainViewModel(
        AddonManager addons,
        SettingsService settings,
        IDialogService dialogs,
        CurseForgeApiClient api,
        DownloadWatcher watcher,
        FileLogSink fileLog,
        ILogSink log)
    {
        _addons = addons;
        _settings = settings;
        _dialogs = dialogs;
        _api = api;
        _watcher = watcher;
        _log = log;
        _fileLog = fileLog;
        _dispatcher = Dispatcher.CurrentDispatcher;

        WowRootPath = settings.Current.WowRootPath;
        ApiKey = settings.Current.CurseForgeApiKey;
        BackupBeforeUpdate = settings.Current.BackupBeforeUpdate;
        WatchDownloads = settings.Current.WatchDownloads;
        AutoInstallDetectedArchives = settings.Current.AutoInstallDetectedArchives;
        DeleteArchiveAfterInstall = settings.Current.DeleteArchiveAfterInstall;
        DownloadsFolder = settings.Current.DownloadsFolder ?? DownloadWatcher.DefaultDownloadsFolder;

        _watcher.ArchiveDetected += OnArchiveDetected;

        ReloadInstallations(settings.Current.SelectedFlavorFolder);
    }

    // ---- Installation / folder selection ------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWowFolder))]
    public partial string? WowRootPath { get; set; }

    public ObservableCollection<WowInstallation> Installations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallation))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(SearchCommand))]
    public partial WowInstallation? SelectedInstallation { get; set; }

    public bool HasWowFolder => !string.IsNullOrWhiteSpace(WowRootPath);

    public bool HasInstallation => SelectedInstallation is not null;

    // ---- Tabs ---------------------------------------------------------------

    [ObservableProperty]
    public partial MainTab SelectedTab { get; set; }

    // ---- Installed addons ---------------------------------------------------

    public ObservableCollection<InstalledAddonViewModel> InstalledAddons { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledSummary))]
    public partial int UpdateCount { get; set; }

    public string InstalledSummary => InstalledAddons.Count == 0
        ? "No addons found"
        : UpdateCount > 0
            ? $"{InstalledAddons.Count} addons · {UpdateCount} with updates"
            : $"{InstalledAddons.Count} addons · all up to date";

    // ---- Search -------------------------------------------------------------

    /// <summary>Bound to the search box above the tabs. Enter runs <see cref="SearchCommand"/>.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The query the current results belong to, for the results header.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    public partial string? ActiveQuery { get; set; }

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    public partial bool IsSearching { get; set; }

    public string SearchSummary => IsSearching
        ? $"Searching for “{ActiveQuery}”…"
        : ActiveQuery is null
            ? "Type a name above and press Enter."
            : SearchResults.Count == 0
                ? $"Nothing found for “{ActiveQuery}”."
                : $"{SearchResults.Count} results for “{ActiveQuery}”";

    // ---- Settings -----------------------------------------------------------

    [ObservableProperty]
    public partial string? ApiKey { get; set; }

    [ObservableProperty]
    public partial bool BackupBeforeUpdate { get; set; }

    [ObservableProperty]
    public partial string? ApiKeyStatus { get; set; }

    public string DataDirectory => _settings.DataDirectory;


    // ---- Installing from a downloaded zip -----------------------------------

    /// <summary>
    /// Watch the downloads folder for addon zips. This is the honest route for
    /// addons whose authors switched off API downloads: the user fetches the file
    /// from the addon's own page and the app only unpacks it.
    /// </summary>
    [ObservableProperty]
    public partial bool WatchDownloads { get; set; }

    [ObservableProperty]
    public partial string? DownloadsFolder { get; set; }

    [ObservableProperty]
    public partial bool AutoInstallDetectedArchives { get; set; }

    [ObservableProperty]
    public partial bool DeleteArchiveAfterInstall { get; set; }

    /// <summary>A zip that turned up in the watched folder and is waiting on the user.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingArchive))]
    [NotifyPropertyChangedFor(nameof(PendingArchiveText))]
    public partial DetectedArchive? PendingArchive { get; set; }

    public bool HasPendingArchive => PendingArchive is not null;

    public string PendingArchiveText => PendingArchive is { } archive
        ? $"Found “{archive.FileName}” — installs {string.Join(", ", archive.Folders)}"
        : string.Empty;

    // ---- Shared status ------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    /// <summary>Shown as a banner when the provider is not usable yet.</summary>
    public string? ProviderHint => _addons.Provider.ConfigurationHint;

    public bool HasProviderHint => ProviderHint is not null;

    // ---- Startup ------------------------------------------------------------

    /// <summary>Called once the window is loaded.</summary>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        _isInitialized = true;

        ApplyWatcherSettings();

        if (SelectedInstallation is null)
        {
            // Nothing remembered: offer a guess so the first run is one click.
            if (!HasWowFolder && WowLocator.TryGuessInstallPath() is { } guess)
            {
                StatusMessage = $"Found a WoW folder at {guess}. Confirm it in Settings.";
                WowRootPath = guess;
                ReloadInstallations(null);
                PersistFolder();
            }
            else
            {
                StatusMessage = "Choose your World of Warcraft folder to get started.";
                SelectedTab = MainTab.Settings;
                return;
            }
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    // ---- Commands -----------------------------------------------------------

    [RelayCommand]
    private void BrowseFolder()
    {
        var picked = _dialogs.PickFolder(
            "Select your World of Warcraft folder",
            WowRootPath ?? WowLocator.TryGuessInstallPath());

        if (picked is null)
            return;

        if (!WowLocator.LooksLikeWowFolder(picked))
        {
            _dialogs.ShowError(
                "That does not look like a WoW folder",
                $"No client folder (_retail_, _classic_era_, …) was found under:{Environment.NewLine}{picked}"
                + $"{Environment.NewLine}{Environment.NewLine}Pick the folder that contains them, or the client folder itself.");
            return;
        }

        WowRootPath = picked;
        ReloadInstallations(null);
        PersistFolder();

        RefreshCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (SelectedInstallation is not { } installation)
            return;

        await CancelPreviousAsync(Interlocked.Exchange(ref _refreshCts, null)).ConfigureAwait(true);
        var cts = new CancellationTokenSource();
        _refreshCts = cts;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);
            var addons = await _addons.GetInstalledAsync(installation, progress, cts.Token).ConfigureAwait(true);

            InstalledAddons.Clear();
            foreach (var addon in addons)
                InstalledAddons.Add(CreateRow(addon));

            RecalculateUpdateCount();
            OnPropertyChanged(nameof(InstalledSummary));
            StatusMessage = InstalledSummary;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (CurseForgeApiException ex)
        {
            StatusMessage = ex.Message;
            if (ex.IsAuthFailure)
                SelectedTab = MainTab.Settings;
        }
        catch (Exception ex)
        {
            _log.Error("Refresh failed", ex);
            StatusMessage = "Could not read the addon folder.";
            _dialogs.ShowError("Refresh failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy && HasInstallation;

    /// <summary>
    /// Requirement: typing in the box above the tabs and pressing Enter searches
    /// the catalog and switches to the Search tab with the results.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length == 0)
            return;

        if (SelectedInstallation is not { } installation)
            return;

        // Navigate first: the user asked for the tab, not for a spinner in place.
        SelectedTab = MainTab.Search;
        ActiveQuery = query;

        await CancelPreviousAsync(Interlocked.Exchange(ref _searchCts, null)).ConfigureAwait(true);
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        IsSearching = true;
        SearchResults.Clear();

        try
        {
            var results = await _addons.SearchAsync(query, installation, cts.Token).ConfigureAwait(true);
            if (cts.Token.IsCancellationRequested)
                return;

            var installedIds = InstalledAddons
                .Select(a => a.Addon.ProviderId)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var result in results)
            {
                SearchResults.Add(new SearchResultViewModel(
                    result,
                    installedIds.Contains(result.ProviderId),
                    InstallFromSearchAsync,
                    _dialogs.OpenUrl));
            }

            StatusMessage = SearchSummary;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search.
        }
        catch (CurseForgeApiException ex)
        {
            StatusMessage = ex.Message;
            if (ex.IsAuthFailure)
                SelectedTab = MainTab.Settings;
        }
        catch (Exception ex)
        {
            _log.Error("Search failed", ex);
            StatusMessage = "Search failed.";
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(SearchSummary));
        }
    }

    private bool CanSearch() => HasInstallation;

    [RelayCommand]
    private async Task UpdateAllAsync(CancellationToken ct)
    {
        var pending = InstalledAddons.Where(a => a.CanUpdate).ToList();
        if (pending.Count == 0)
        {
            StatusMessage = "Everything is up to date.";
            return;
        }

        foreach (var row in pending)
        {
            if (ct.IsCancellationRequested)
                break;

            await UpdateAddonAsync(row, ct).ConfigureAwait(true);
        }

        StatusMessage = InstalledSummary;
    }

    [RelayCommand]
    private async Task SaveApiKeyAsync()
    {
        _settings.Current.CurseForgeApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        _settings.Save();

        OnPropertyChanged(nameof(ProviderHint));
        OnPropertyChanged(nameof(HasProviderHint));

        if (!_settings.Current.HasApiKey)
        {
            ApiKeyStatus = "Key cleared. Search and update checks are disabled.";
            return;
        }

        ApiKeyStatus = "Checking…";
        try
        {
            ApiKeyStatus = await _api.ValidateKeyAsync().ConfigureAwait(true)
                ? "Key accepted."
                : "CurseForge rejected this key.";
        }
        catch (CurseForgeApiException ex)
        {
            ApiKeyStatus = ex.Message;
        }

        if (HasInstallation)
            await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GoToSettings() => SelectedTab = MainTab.Settings;

    // ---- Zip installation ---------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasInstallation))]
    private async Task InstallFromZipAsync(CancellationToken ct)
    {
        var picked = _dialogs.PickFile(
            "Select a downloaded addon archive",
            "Addon archives (*.zip)|*.zip|All files (*.*)|*.*",
            DownloadsFolder);

        if (picked is null)
            return;

        var inspection = ZipAddonInspector.Inspect(picked);
        if (!inspection.IsAddon)
        {
            _dialogs.ShowError(
                "That archive is not a WoW addon",
                inspection.Reason ?? "The archive contains no addon folders.");
            return;
        }

        await InstallArchiveAsync(new DetectedArchive(picked, inspection.Folders), ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallPendingArchiveAsync(CancellationToken ct)
    {
        if (PendingArchive is not { } archive)
            return;

        PendingArchive = null;
        await InstallArchiveAsync(archive, ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissPendingArchive() => PendingArchive = null;

    [RelayCommand]
    private void BrowseDownloadsFolder()
    {
        var picked = _dialogs.PickFolder("Select the folder to watch for addon archives", DownloadsFolder);
        if (picked is null)
            return;

        DownloadsFolder = picked;
    }

    private async Task InstallArchiveAsync(DetectedArchive archive, CancellationToken ct)
    {
        if (SelectedInstallation is not { } installation)
            return;

        IsBusy = true;
        try
        {
            var progress = new Progress<InstallProgress>(p => StatusMessage = $"{p.Stage} {archive.FileName}…");
            var folders = await _addons
                .InstallFromArchiveAsync(archive.Path, installation, progress, ct)
                .ConfigureAwait(true);

            StatusMessage = $"Installed {string.Join(", ", folders)} from {archive.FileName}.";

            if (DeleteArchiveAfterInstall)
                TryDeleteArchive(archive.Path);

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded or shutting down.
        }
        catch (AddonInstallException ex)
        {
            _log.Error($"Installing '{archive.Path}' failed", ex);
            _dialogs.ShowError("Could not install the archive", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error($"Installing '{archive.Path}' failed", ex);
            _dialogs.ShowError("Could not install the archive", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void TryDeleteArchive(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not delete '{path}': {ex.Message}");
        }
    }

    /// <summary>Raised on a watcher thread, so marshal before touching bound state.</summary>
    private void OnArchiveDetected(DetectedArchive archive)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnArchiveDetected(archive));
            return;
        }

        if (AutoInstallDetectedArchives && HasInstallation)
        {
            InstallPendingArchiveCommand.Cancel();
            PendingArchive = archive;
            InstallPendingArchiveCommand.Execute(null);
            return;
        }

        PendingArchive = archive;
        StatusMessage = PendingArchiveText;
    }

    private void ApplyWatcherSettings()
    {
        // The constructor assigns these properties, which fires the change
        // handlers below before there is any point in watching anything.
        if (!_isInitialized)
            return;

        if (WatchDownloads)
            _watcher.Start(DownloadsFolder ?? DownloadWatcher.DefaultDownloadsFolder);
        else
            _watcher.Stop();
    }

    partial void OnWatchDownloadsChanged(bool value)
    {
        _settings.Current.WatchDownloads = value;
        _settings.Save();
        ApplyWatcherSettings();
    }

    partial void OnDownloadsFolderChanged(string? value)
    {
        _settings.Current.DownloadsFolder = value;
        _settings.Save();
        ApplyWatcherSettings();
    }

    partial void OnAutoInstallDetectedArchivesChanged(bool value)
    {
        _settings.Current.AutoInstallDetectedArchives = value;
        _settings.Save();
    }

    partial void OnDeleteArchiveAfterInstallChanged(bool value)
    {
        _settings.Current.DeleteArchiveAfterInstall = value;
        _settings.Save();
    }

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenUrl(_settings.DataDirectory);

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (_fileLog.Directory is { Length: > 0 } directory)
            _dialogs.OpenUrl(directory);
    }

    [RelayCommand]
    private void OpenApiKeyPage() => _dialogs.OpenUrl("https://console.curseforge.com/");

    // ---- Install / update ---------------------------------------------------

    private async Task UpdateAddonAsync(InstalledAddonViewModel row, CancellationToken ct)
    {
        if (row.Addon.LatestRelease is not { } release || SelectedInstallation is not { } installation)
            return;

        var previousStatus = row.Status;
        row.Status = AddonStatus.Installing;
        row.ReportProgress(new InstallProgress("Starting", null));

        try
        {
            var progress = new Progress<InstallProgress>(row.ReportProgress);
            await _addons.InstallAsync(release, installation, progress, ct).ConfigureAwait(true);

            // The release's folder list is authoritative after an install, but a
            // component's title and version are only known from a fresh scan, so
            // carry over what was already known and let Refresh fill the rest.
            var components = release.Folders.Count > 0
                ? release.Folders
                    .Select(folder => row.Addon.Components.FirstOrDefault(c =>
                        string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase))
                        ?? new AddonComponent(folder, null, release.Version))
                    .ToList()
                : row.Addon.Components;

            row.Apply(new InstalledAddon
            {
                Name = row.Addon.Name,
                Components = components,
                InstalledVersion = release.Version,
                Author = row.Addon.Author,
                ProviderName = row.Addon.ProviderName,
                ProviderId = row.Addon.ProviderId,
                WebsiteUrl = row.Addon.WebsiteUrl,
                ThumbnailUrl = row.Addon.ThumbnailUrl,
                LatestRelease = release,
                Status = AddonStatus.UpToDate,
            });

            StatusMessage = $"Updated {row.Name} to {release.Version}.";
        }
        catch (OperationCanceledException)
        {
            row.Status = previousStatus;
        }
        catch (AddonInstallException ex)
        {
            row.StatusDetail = ex.Message;
            row.Status = AddonStatus.Failed;
            _log.Error($"Update of '{row.Name}' failed", ex);
            _dialogs.ShowError($"Could not update {row.Name}", ex.Message);
        }
        catch (Exception ex)
        {
            row.StatusDetail = ex.Message;
            row.Status = AddonStatus.Failed;
            _log.Error($"Update of '{row.Name}' failed", ex);
        }
        finally
        {
            row.Progress = null;
            row.ProgressStage = null;
            RecalculateUpdateCount();
        }
    }

    private async Task InstallFromSearchAsync(SearchResultViewModel row, CancellationToken ct)
    {
        if (SelectedInstallation is not { } installation)
            return;

        row.IsBusy = true;
        try
        {
            // The search payload's release may be stale; re-resolve before writing files.
            var release = await _addons
                .GetLatestReleaseAsync(row.Result.ProviderId, installation, ct)
                .ConfigureAwait(true)
                ?? row.Result.LatestRelease;

            if (release is null || !release.IsDownloadable)
            {
                _dialogs.ShowError(
                    $"Cannot install {row.Name}",
                    "No downloadable release matches this game version. Open the addon page to install it manually.");
                return;
            }

            var progress = new Progress<InstallProgress>(row.ReportProgress);
            await _addons.InstallAsync(release, installation, progress, ct).ConfigureAwait(true);

            row.IsInstalled = true;
            StatusMessage = $"Installed {row.Name} {release.Version}.";

            // Bring the new addon into the installed table.
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // User navigated away.
        }
        catch (AddonInstallException ex)
        {
            _log.Error($"Install of '{row.Name}' failed", ex);
            _dialogs.ShowError($"Could not install {row.Name}", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error($"Install of '{row.Name}' failed", ex);
            _dialogs.ShowError($"Could not install {row.Name}", ex.Message);
        }
        finally
        {
            row.IsBusy = false;
            row.Progress = null;
            row.ProgressStage = null;
        }
    }

    // ---- Plumbing -----------------------------------------------------------

    private InstalledAddonViewModel CreateRow(InstalledAddon addon) =>
        new(addon, UpdateAddonAsync, _dialogs.OpenUrl);

    private void RecalculateUpdateCount()
    {
        UpdateCount = InstalledAddons.Count(a => a.CanUpdate);
        OnPropertyChanged(nameof(InstalledSummary));
    }

    private void ReloadInstallations(string? preferredFlavorFolder)
    {
        Installations.Clear();

        foreach (var installation in WowLocator.FindInstallations(WowRootPath))
            Installations.Add(installation);

        SelectedInstallation = Installations.FirstOrDefault(i =>
                string.Equals(i.FlavorFolder, preferredFlavorFolder, StringComparison.OrdinalIgnoreCase))
            ?? Installations.FirstOrDefault();
    }

    private void PersistFolder()
    {
        _settings.Current.WowRootPath = WowRootPath;
        _settings.Current.SelectedFlavorFolder = SelectedInstallation?.FlavorFolder;
        _settings.Save();
    }

    partial void OnSelectedInstallationChanged(WowInstallation? value)
    {
        if (value is null)
            return;

        _settings.Current.SelectedFlavorFolder = value.FlavorFolder;
        _settings.Save();

        // Skip during construction; the window's Loaded handler does the first scan.
        if (_isInitialized)
            RefreshCommand.Execute(null);
    }

    partial void OnBackupBeforeUpdateChanged(bool value)
    {
        _settings.Current.BackupBeforeUpdate = value;
        _settings.Save();
    }

    /// <summary>
    /// Cancels and disposes the previous token source for an operation, so a
    /// second refresh or search supersedes the one already in flight.
    /// </summary>
    private static async Task CancelPreviousAsync(CancellationTokenSource? previous)
    {
        if (previous is null)
            return;

        await previous.CancelAsync().ConfigureAwait(true);
        previous.Dispose();
    }
}
