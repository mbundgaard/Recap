using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Recap.Settings;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string AppFolder { get; }
    public string SettingsPath { get; }

    public AppSettingsStore()
    {
        // Use the actual executable location, not the working or bundle extraction directory.
        var executableFolder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var portableSettingsPath = Path.Combine(executableFolder, "settings.json");
        SettingsPath = File.Exists(portableSettingsPath)
            ? portableSettingsPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recap", "settings.json");

        AppFolder = Path.GetDirectoryName(SettingsPath) ?? AppContext.BaseDirectory;
    }

    public AppSettings LoadOrCreate()
    {
        Directory.CreateDirectory(AppFolder);

        if (!File.Exists(SettingsPath))
        {
            var settings = CreateDefaultSettings();
            Save(settings);
            return settings;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefaultSettings();
            ApplyDefaults(settings);
            Save(settings);
            return settings;
        }
        catch
        {
            var backupPath = SettingsPath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(SettingsPath, backupPath, overwrite: true);

            var settings = CreateDefaultSettings();
            Save(settings);
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppFolder);
        Recap.Storage.AtomicFile.WriteText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public void OpenSettingsFile()
    {
        LoadOrCreate();
        Process.Start(new ProcessStartInfo(SettingsPath) { UseShellExecute = true });
    }

    public static AppSettings CreateDefaultSettings()
    {
        var appFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recap");
        return new AppSettings
        {
            RecordingsFolder = Path.Combine(appFolder, "Recordings"),
            LogsFolder = Path.Combine(appFolder, "Logs"),
            AudioDevices = new AudioDeviceSettings(),
            RecordingProcessing = new RecordingProcessingSettings(),
            AzureSpeech = new AzureSpeechSettings(),
            OpenAiCompatible = new OpenAiCompatibleSettings()
        };
    }

    private static void ApplyDefaults(AppSettings settings)
    {
        var defaults = CreateDefaultSettings();

        if (string.IsNullOrWhiteSpace(settings.RecordingsFolder))
        {
            settings.RecordingsFolder = defaults.RecordingsFolder;
        }

        if (string.IsNullOrWhiteSpace(settings.LogsFolder))
        {
            var recordingsFolder = Environment.ExpandEnvironmentVariables(settings.RecordingsFolder);
            var appFolder = Directory.GetParent(recordingsFolder)?.FullName
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recap");
            settings.LogsFolder = Path.Combine(appFolder, "Logs");
        }

        if (string.IsNullOrWhiteSpace(settings.SpeechProviderId)) settings.SpeechProviderId = defaults.SpeechProviderId;
        if (string.IsNullOrWhiteSpace(settings.LlmProviderId)) settings.LlmProviderId = defaults.LlmProviderId;

        settings.AudioDevices ??= defaults.AudioDevices;
        settings.RecordingProcessing ??= defaults.RecordingProcessing;
        settings.LocalWhisper ??= defaults.LocalWhisper;
        settings.AzureSpeech ??= defaults.AzureSpeech;
        settings.GrokSpeech ??= defaults.GrokSpeech;
        if (string.IsNullOrWhiteSpace(settings.GrokSpeech.Model)) settings.GrokSpeech.Model = GrokSpeechSettings.DefaultModel;
        settings.OpenAiCompatible ??= defaults.OpenAiCompatible;

        if (string.IsNullOrWhiteSpace(settings.AzureSpeech.ResourceName)) settings.AzureSpeech.ResourceName = defaults.AzureSpeech.ResourceName;
        if (string.IsNullOrWhiteSpace(settings.AzureSpeech.Region)) settings.AzureSpeech.Region = defaults.AzureSpeech.Region;
        if (string.IsNullOrWhiteSpace(settings.AzureSpeech.Endpoint)) settings.AzureSpeech.Endpoint = defaults.AzureSpeech.Endpoint;
        if (string.IsNullOrWhiteSpace(settings.AzureSpeech.RecognitionLanguage)) settings.AzureSpeech.RecognitionLanguage = defaults.AzureSpeech.RecognitionLanguage;
    }
}
