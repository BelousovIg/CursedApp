using System.Text.Json;

namespace CursedApp.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under %APPDATA%\CursedApp.
/// Saves are debounced and written through a temp file so a crash mid-write
/// cannot leave a truncated settings file behind.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;
    private readonly ILogSink _log;
    private readonly Lock _gate = new();

    public SettingsService(ILogSink log)
    {
        _log = log;
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CursedApp");
        Directory.CreateDirectory(DataDirectory);

        _settingsPath = Path.Combine(DataDirectory, "settings.json");
        Current = Load();
    }

    public string DataDirectory { get; }

    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

    public AppSettings Current { get; private set; }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                if (JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) is { } loaded)
                    return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn($"Settings could not be read, starting from defaults: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(Current, JsonOptions);
                var tempPath = _settingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("Could not save settings", ex);
            }
        }
    }
}
