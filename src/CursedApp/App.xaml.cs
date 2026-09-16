using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Threading;
using CursedApp.Services;
using CursedApp.Services.Providers;
using CursedApp.Services.Providers.CurseForge;
using CursedApp.ViewModels;
using CursedApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CursedApp;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogSink? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
        _log = _services.GetRequiredService<ILogSink>();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log?.Error("Unhandled exception", args.ExceptionObject as Exception);

        _log.Info("CursedApp starting.");

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logs sit next to settings.json, one file per day.
        var logSink = new FileLogSink(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CursedApp",
            "logs"));

        services.AddSingleton<ILogSink>(logSink);
        services.AddSingleton(logSink);
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IDialogService, DialogService>();

        // The API client talks to api.curseforge.com; the installer follows
        // whatever absolute download URL the API hands back, so it gets its own.
        services.AddHttpClient<CurseForgeApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.curseforge.com");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CursedApp/1.0");
        });

        services.AddHttpClient<AddonInstaller>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CursedApp/1.0");
        });

        services.AddSingleton<DownloadWatcher>();
        services.AddSingleton<AddonFolderScanner>();
        services.AddSingleton<IAddonProvider, CurseForgeAddonProvider>();
        services.AddSingleton<AddonManager>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);

        MessageBox.Show(
            $"Something went wrong:{Environment.NewLine}{Environment.NewLine}{e.Exception.Message}",
            "CursedApp",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep the window alive; the operation that failed has already been abandoned.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("CursedApp exiting.");
        _services?.Dispose();
        base.OnExit(e);
    }
}
