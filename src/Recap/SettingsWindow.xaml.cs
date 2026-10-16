using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using NAudio.CoreAudioApi;
using Recap.Recording;
using Recap.Settings;
using Recap.Updates;

namespace Recap;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsStore _store;
    private AppSettings _settings;
    public bool SpeechRecognitionPaused => _settings.SpeechRecognitionPaused;
    public bool CheckForUpdatesOnStartup => _settings.CheckForUpdatesOnStartup;
    public bool CheckForUpdatesRequested { get; private set; }

    public ObservableCollection<AudioDeviceOption> RemoteDevices { get; } = [];
    public ObservableCollection<AudioDeviceOption> MicrophoneDevices { get; } = [];

    public SettingsWindow(AppSettingsStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;

        InitializeComponent();
        InitializeLocalWhisper();
        SettingsNavigation.SelectedIndex = 0;
        LoadAudioDevices();
        LoadFields();
    }

    private void SettingsNavigation_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (StorageSection is null) return;
        var generalVisibility = SettingsNavigation.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileSection.Visibility = generalVisibility;
        StorageSection.Visibility = generalVisibility;
        DevicesSection.Visibility = generalVisibility;
        SpeechSection.Visibility = SettingsNavigation.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        LlmSection.Visibility = SettingsNavigation.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsContentScroll.ScrollToTop();
    }

    private void LoadFields()
    {
        SettingsPathText.Text = _store.SettingsPath;
        UserNameBox.Text = _settings.UserName;
        AutomaticProcessingBox.IsChecked = !_settings.SpeechRecognitionPaused;
        CheckForUpdatesBox.IsChecked = _settings.CheckForUpdatesOnStartup;
        UpdateVersionText.Text = $"Installed version: {UpdateTrust.CurrentVersion}";

        RecordingsFolderBox.Text = _settings.RecordingsFolder;
        LogsFolderBox.Text = _settings.LogsFolder;
        PreserveRawFilesBox.IsChecked = _settings.RecordingProcessing.PreserveRawFiles;
        RemoteDeviceBox.ItemsSource = RemoteDevices;
        MicrophoneDeviceBox.ItemsSource = MicrophoneDevices;
        RemoteDeviceBox.SelectedItem = RemoteDevices.FirstOrDefault(x => x.Id == _settings.AudioDevices.RemoteDeviceId) ?? RemoteDevices.FirstOrDefault();
        MicrophoneDeviceBox.SelectedItem = MicrophoneDevices.FirstOrDefault(x => x.Id == _settings.AudioDevices.MicrophoneDeviceId) ?? MicrophoneDevices.FirstOrDefault();

        SpeechProviderIdBox.SelectedValue = _settings.SpeechProviderId;
        LlmProviderIdBox.SelectedValue = _settings.LlmProviderId;

        AzureSpeechResourceNameBox.Text = _settings.AzureSpeech.ResourceName;
        AzureSpeechEndpointBox.Text = _settings.AzureSpeech.Endpoint;
        AzureSpeechKeyBox.Password = _settings.AzureSpeech.SubscriptionKey;
        GrokSpeechApiKeyBox.Password = _settings.GrokSpeech.ApiKey;

        OpenAiEndpointBox.Text = _settings.OpenAiCompatible.Endpoint;
        OpenAiKeyBox.Password = _settings.OpenAiCompatible.ApiKey;
        ModelDeploymentBox.Text = _settings.OpenAiCompatible.ModelOrDeploymentName;
        LoadLocalFields(_settings.LocalWhisper);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SpeechProviderIdBox.SelectedValue is not string speechProviderId
            || LlmProviderIdBox.SelectedValue is not string llmProviderId)
        {
            MessageBox.Show(this, "Select a supported speech and AI provider before saving.",
                "Provider required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Recap.LocalWhisper.LocalWhisperSettings local;
        try { local = ReadLocalFields(); }
        catch (ArgumentException) when (speechProviderId != "local-whisper")
        {
            // Invalid hidden fields must not prevent saving a cloud provider.
            // Keep the last saved local configuration until those edits are valid.
            local = _settings.LocalWhisper;
        }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Local settings", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _settings.LocalWhisper = local;
        _settings.SpeechRecognitionPaused = AutomaticProcessingBox.IsChecked != true;
        _settings.CheckForUpdatesOnStartup = CheckForUpdatesBox.IsChecked == true;
        _settings.UserName = UserNameBox.Text.Trim();
        _settings.RecordingsFolder = RecordingsFolderBox.Text.Trim();
        _settings.LogsFolder = LogsFolderBox.Text.Trim();
        _settings.RecordingProcessing.PreserveRawFiles = PreserveRawFilesBox.IsChecked == true;
        _settings.AudioDevices.RemoteDeviceId = (RemoteDeviceBox.SelectedItem as AudioDeviceOption)?.Id;
        _settings.AudioDevices.MicrophoneDeviceId = (MicrophoneDeviceBox.SelectedItem as AudioDeviceOption)?.Id;

        _settings.SpeechProviderId = speechProviderId;
        _settings.LlmProviderId = llmProviderId;

        _settings.AzureSpeech.ResourceName = AzureSpeechResourceNameBox.Text.Trim();
        _settings.AzureSpeech.Endpoint = AzureSpeechEndpointBox.Text.Trim();
        _settings.AzureSpeech.SubscriptionKey = AzureSpeechKeyBox.Password;
        _settings.GrokSpeech.ApiKey = GrokSpeechApiKeyBox.Password;

        _settings.OpenAiCompatible.Endpoint = OpenAiEndpointBox.Text.Trim();
        _settings.OpenAiCompatible.ApiKey = OpenAiKeyBox.Password;
        _settings.OpenAiCompatible.ModelOrDeploymentName = ModelDeploymentBox.Text.Trim();

        _store.Save(_settings);
        DialogResult = true;
        Close();
    }

    private void SaveAndCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesRequested = true;
        Save_Click(sender, e);
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        Recap.Agent.AgentSettings.Copy(_store.LoadOrCreate(), _settings);
        LoadFields();
    }

    private void OpenJson_Click(object sender, RoutedEventArgs e)
    {
        _store.OpenSettingsFile();
    }

    private void LoadAudioDevices()
    {
        RemoteDevices.Clear();
        MicrophoneDevices.Clear();

        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                RemoteDevices.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
            }

            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                MicrophoneDevices.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not list audio devices", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
