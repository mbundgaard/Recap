using System.Windows;
using Recap.Logging;
using Recap.Settings;

namespace Recap;

public partial class App : Application
{
    public static AppSettingsStore SettingsStore { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        mainWindow.Show();
    }
}
