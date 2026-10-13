using System.Windows;
using Recap.Logging;
using Recap.Settings;
using Recap.Updates;

namespace Recap;

public partial class App : Application
{
    public static AppSettingsStore SettingsStore { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdateInstaller.ApplyAsync(e.Args[1]));
            return;
        }

        SettingsStore = new AppSettingsStore();
        Settings = SettingsStore.LoadOrCreate();
        AppLogger.Initialize(Settings.LogsFolder);
        AppLogger.Info($"Settings path: {SettingsStore.SettingsPath}");

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error(args.Exception, "Unhandled UI exception.");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLogger.Error(exception, "Unhandled app-domain exception.");
            }
        };

        var mainWindow = new MainWindow(SettingsStore, Settings);
        MainWindow = mainWindow;
        if (e.Args.Length == 2 && e.Args[0] == "--update-complete")
        {
            EventHandler? acknowledge = null;
            acknowledge = (_, _) =>
            {
                mainWindow.ContentRendered -= acknowledge;
                try { UpdateInstaller.MarkStartupSuccessful(e.Args[1]); }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "Could not acknowledge update startup; helper will roll back.");
                    Shutdown(1);
                }
            };
            mainWindow.ContentRendered += acknowledge;
        }
        mainWindow.Show();
    }
}
