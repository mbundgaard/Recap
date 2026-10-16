using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Recap.LocalWhisper;

namespace Recap;

public partial class SettingsWindow
{
    private int _localBatchSize = 1;
    private readonly DispatcherTimer _localStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void InitializeLocalWhisper()
    {
        EnglishModelBox.ItemsSource = LocalWhisperSettings.Models;
        DanishModelBox.ItemsSource = LocalWhisperSettings.Models;
        _localStatusTimer.Tick += (_, _) => RefreshLocalStatus();
        _localStatusTimer.Start();
    }
    private void LoadLocalFields(LocalWhisperSettings settings)
    {
        EnglishModelBox.SelectedValue = settings.EnglishModel;
        DanishModelBox.SelectedValue = settings.DanishModel;
        LocalDeviceBox.SelectedValue = settings.Device;
        LocalComputeBox.SelectedValue = settings.ComputeType;
        _localBatchSize = settings.BatchSize;
        LocalThreadsBox.Text = settings.CpuThreads.ToString();
        LocalGpuBox.Text = settings.GpuIndex.ToString();
        UpdateSpeechProviderPanels();
        RefreshLocalStatus();
    }

    private LocalWhisperSettings ReadLocalFields()
    {
        static int Number(TextBox box)
        {
            if (!int.TryParse(box.Text.Trim(), out var value)) throw new ArgumentException("Enter whole numbers in the local processing fields.");
            return value;
        }
        var settings = new LocalWhisperSettings
        {
            EnglishModel = EnglishModelBox.SelectedValue as string ?? "base",
            DanishModel = DanishModelBox.SelectedValue as string ?? "medium",
            Device = LocalDeviceBox.SelectedValue as string ?? "",
            ComputeType = LocalComputeBox.SelectedValue as string ?? "",
            BatchSize = _localBatchSize, CpuThreads = Number(LocalThreadsBox), GpuIndex = Number(LocalGpuBox),
            SpeakerCount = 0
        };
        settings.Validate();
        return settings;
    }
    private void SpeechProvider_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSpeechProviderPanels();
    private void UpdateSpeechProviderPanels()
    {
        if (LocalWhisperPanel is null || AzureSpeechPanel is null || GrokSpeechPanel is null) return;
        var provider = SpeechProviderIdBox.SelectedValue as string;
        LocalWhisperPanel.Visibility = provider == "local-whisper" ? Visibility.Visible : Visibility.Collapsed;
        AzureSpeechPanel.Visibility = provider == "azure-speech" ? Visibility.Visible : Visibility.Collapsed;
        GrokSpeechPanel.Visibility = provider == "grok-speech" ? Visibility.Visible : Visibility.Collapsed;
        RefreshLocalStatus();
    }
    private void LocalModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshLocalStatus();
    }
    private void RefreshLocalStatus()
    {
        if (LocalStatusText is null || EnglishModelBox.SelectedValue is not string || DanishModelBox.SelectedValue is not string) return;
        try
        {
            var selected = ReadLocalFields();
            var status = LocalWhisperRuntime.Status(selected.ForLanguage("en-US"));
            LocalSetupProgress.Value = status.Percent;
            LocalSetupProgress.IsIndeterminate = status.SetupRunning && status.Percent == 0;
            LocalSetupProgress.Visibility = status.SetupRunning ? Visibility.Visible : Visibility.Collapsed;
            LocalStatusText.Text = status.SetupRunning ? status.Message : "";
            LocalWhisperControls.IsEnabled = !status.SetupRunning;
            if (!LocalWhisperRuntime.Supported) LocalStatusText.Text = "Local Whisper currently requires Windows x64.";
        }
        catch (ArgumentException)
        {
            LocalSetupProgress.Visibility = Visibility.Collapsed;
            LocalStatusText.Text = "Check the advanced settings values.";
        }
        LocalStatusText.Visibility = string.IsNullOrWhiteSpace(LocalStatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _localStatusTimer.Stop();
        base.OnClosing(e);
    }
}
