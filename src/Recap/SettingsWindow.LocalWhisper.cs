using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Recap.LocalWhisper;

namespace Recap;

public partial class SettingsWindow
{
    private Task _localWork = Task.CompletedTask;
    private CancellationTokenSource? _localCancellation;
    private bool _localClosing;
    private string? _localFeedback;
    private readonly DispatcherTimer _localStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void InitializeLocalWhisper()
    {
        EnglishModelBox.ItemsSource = LocalWhisperSettings.Models;
        DanishModelBox.ItemsSource = LocalWhisperSettings.Models;
        LocalFolderBox.Text = LocalWhisperSettings.Root;
        _localStatusTimer.Tick += (_, _) => RefreshLocalStatus();
        _localStatusTimer.Start();
    }
    private void LoadLocalFields(LocalWhisperSettings settings)
    {
        EnglishModelBox.SelectedValue = settings.EnglishModel;
        DanishModelBox.SelectedValue = settings.DanishModel;
        LocalDeviceBox.SelectedValue = settings.Device;
        LocalComputeBox.SelectedValue = settings.ComputeType;
        LocalBatchBox.Text = settings.BatchSize.ToString();
        LocalThreadsBox.Text = settings.CpuThreads.ToString();
        LocalGpuBox.Text = settings.GpuIndex.ToString();
        LocalSpeakersBox.Text = settings.SpeakerCount.ToString();
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
            BatchSize = Number(LocalBatchBox), CpuThreads = Number(LocalThreadsBox), GpuIndex = Number(LocalGpuBox),
            SpeakerCount = Number(LocalSpeakersBox)
        };
        settings.Validate();
        return settings;
    }
    private void SpeechProvider_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSpeechProviderPanels();
    private void UpdateSpeechProviderPanels()
    {
        if (LocalWhisperPanel is null || AzureSpeechPanel is null) return;
        var local = SpeechProviderIdBox.SelectedValue as string == "local-whisper";
        LocalWhisperPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        AzureSpeechPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        RefreshLocalStatus();
    }
    private void LocalModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _localFeedback = null;
        RefreshLocalStatus();
    }
    private void RefreshLocalStatus()
    {
        if (LocalStatusText is null || EnglishModelBox.SelectedValue is not string || DanishModelBox.SelectedValue is not string) return;
        try
        {
            var selected = ReadLocalFields();
            var status = LocalWhisperRuntime.Status(selected.ForLanguage("en-US"));
            var readiness = LocalWhisperRuntime.StatusSelected(selected);
            var ownBusy = !_localWork.IsCompleted;
            LocalSetupProgress.Value = status.Percent;
            LocalSetupProgress.IsIndeterminate = status.SetupRunning && status.Percent == 0;
            LocalStatusText.Text = status.SetupRunning ? status.Message
                : string.Join("\n", readiness.Select(item =>
                    $"{(item.Language == "en-US" ? "English" : item.Language == "da-DK" ? "Danish" : item.Language)} ({item.Model}): CPU {(item.CpuReady ? "verified" : "setup required")}; GPU {(item.CudaReady ? "verified" : "setup required")}."))
                    + "\n" + (_localFeedback ?? "Missing runtime and models download automatically when processing starts, then verify offline. First use can download several GB.");
            LocalWhisperControls.IsEnabled = !ownBusy && !status.SetupRunning;
            SpeechProviderIdBox.IsEnabled = !ownBusy;
            SaveSettingsButton.IsEnabled = !ownBusy;
            if (!LocalWhisperRuntime.Supported) LocalStatusText.Text = "Local Whisper currently requires Windows x64.";
        }
        catch (ArgumentException) { LocalStatusText.Text = "Complete the local settings fields to check readiness."; }
    }

    private async Task LocalOperationAsync(Func<CancellationToken, Task> work, bool showCompletion = true)
    {
        if (!_localWork.IsCompleted) return;
        _localCancellation?.Dispose();
        _localCancellation = new CancellationTokenSource();
        try
        {
            _localFeedback = null;
            _localWork = work(_localCancellation.Token);
            RefreshLocalStatus();
            await _localWork;
            _localFeedback = showCompletion ? "Operation completed. Save settings to apply your selections." : null;
        }
        catch (OperationCanceledException) { _localFeedback = "Detection cancelled."; }
        catch (Exception ex)
        {
            _localFeedback = ex.Message;
            if (!_localClosing) MessageBox.Show(this, ex.Message, "Local Whisper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshLocalStatus(); }
    }

    private async void DetectLocal_Click(object sender, RoutedEventArgs e)
    {
        LocalWhisperSettings selected;
        try { selected = ReadLocalFields(); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Local settings"); return; }
        await LocalOperationAsync(async token =>
        {
            var heavierModel = LocalWhisperSettings.Models
                .Where(model => model.Id == selected.EnglishModel || model.Id == selected.DanishModel)
                .OrderByDescending(model => Math.Max(model.SuggestedCpuGb, model.SuggestedGpuGb))
                .First();
            var language = heavierModel.Id == selected.DanishModel ? "da-DK" : "en-US";
            var recommendation = HardwareDetection.Recommend(selected.ForLanguage(language), await HardwareDetection.DetectAsync(token));
            token.ThrowIfCancellationRequested();
            selected.Device = recommendation.Settings.Device;
            selected.ComputeType = recommendation.Settings.ComputeType;
            selected.CpuThreads = recommendation.Settings.CpuThreads;
            selected.BatchSize = recommendation.Settings.BatchSize;
            selected.GpuIndex = recommendation.Settings.GpuIndex;
            LoadLocalFields(selected);
        }, showCompletion: false);
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_localWork.IsCompleted)
        {
            e.Cancel = true;
            if (_localClosing) return;
            _localClosing = true;
            _localCancellation?.Cancel();
            try { await _localWork; } catch { }
            Close();
            return;
        }
        _localStatusTimer.Stop();
        _localCancellation?.Dispose();
        base.OnClosing(e);
    }
}
