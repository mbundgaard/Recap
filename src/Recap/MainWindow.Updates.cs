using System.Windows;
using Recap.Logging;
using Recap.Updates;

namespace Recap;

public partial class MainWindow
{
    private static readonly TimeSpan UpdatePollInterval = TimeSpan.FromHours(4);
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly GitHubUpdateService _updates = new();
    private UpdatePreparation? _updatePreparation;
    private CancellationTokenSource? _updatePollingCancellation;
    private Task _updatePollTask = Task.CompletedTask;
    private AvailableUpdate? _availableUpdate => _updatePreparation?.Available;
    private bool _isCheckingUpdates => _updatePreparation?.Checking == true;
    private bool _isDownloadingUpdate => _updatePreparation?.Downloading == true;
    private string? _readyUpdateDirectory => _updatePreparation?.ReadyDirectory;
    private string? _readyUpdateVersion => _updatePreparation?.ReadyVersion;
    private bool _isUpdating;
    private bool _handoffToUpdater;
    private bool _cancelingUpdateShutdown;
    private Task _installTask = Task.CompletedTask;

    private bool WorkPreventsUpdate => Recap.LocalWhisper.LocalWhisperRuntime.SetupRunning || _session is not null || _recordingStarted is not null
        || !_recordingStopTask.IsCompleted || !_processingTask.IsCompleted || _isPausingProcessing
        || _meetingWindows.Values.Any(window => window.IsBusy)
        || OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

    private bool CanRestartForUpdate => _readyUpdateDirectory is not null && !WorkPreventsUpdate
        && !_isUpdating && !_isCheckingUpdates && !_isDownloadingUpdate && !_updateCancellation.IsCancellationRequested;

    private UpdatePreparation PreparedUpdates
    {
        get
        {
            if (_updatePreparation is null)
            {
                _updatePreparation = new UpdatePreparation(_updates.CheckAsync, _updates.DownloadAsync);
                _updatePreparation.Changed += RefreshUpdateIndicator;
            }
            return _updatePreparation;
        }
    }

    private void ConfigureUpdatePolling()
    {
        // Cancellation precedes the publication-support check so opt-out always stops
        // existing work, including a poll that began before settings changed.
        if (!_settings.CheckForUpdatesOnStartup || _updateCancellation.IsCancellationRequested || _agentClosing)
        {
            _updatePollingCancellation?.Cancel();
            _updatePollingCancellation = null;
            return;
        }
        if (!UpdateTrust.SupportsInstallation || _updatePollingCancellation is not null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
        var previous = _updatePollTask;
        _updatePollingCancellation = cancellation;
        _updatePollTask = PollForUpdatesAsync(previous, cancellation);
    }

    private async Task PollForUpdatesAsync(Task previous, CancellationTokenSource cancellation)
    {
        try
        {
            // Rapid off/on toggles wait for the old cancelled download to settle.
            // Its finally block must never clear/dispose the new loop's CTS.
            try { await previous; } catch { }
            var token = cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                await CheckForUpdatesAsync(false, token);
                await Task.Delay(UpdatePollInterval, token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { AppLogger.Warning($"Background update polling stopped: {ex.Message}"); }
        finally
        {
            if (ReferenceEquals(_updatePollingCancellation, cancellation)) _updatePollingCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelBackgroundUpdates()
    {
        _updatePollingCancellation?.Cancel();
        _updatePollingCancellation = null;
        _updateCancellation.Cancel();
    }

    // Shared non-modal API/UI preparation path. Never installs or disables the app.
    private async Task CheckAndDownloadUpdateAsync(CancellationToken cancellationToken = default, bool waitForStoppedPoll = true)
    {
        if (_isUpdating) throw new InvalidOperationException("An update restart is already in progress.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token, cancellationToken);
        // A manual/API request after opt-out starts fresh, rather than joining the
        // cancelled single-flight operation that the previous poll is draining.
        // The polling caller opts out of this wait to avoid awaiting itself.
        if (waitForStoppedPoll && _updatePollingCancellation is null && !_updatePollTask.IsCompleted)
            await _updatePollTask.WaitAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        await PreparedUpdates.CheckAndDownloadAsync(linked.Token);
    }

    private async Task CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (_isUpdating || _updateCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested) return;
        if (!UpdateTrust.SupportsInstallation)
        {
            if (manual) MessageBox.Show(this, "In-app updates are available in published Windows x64 single-file releases. Development builds and ARM64 builds must be updated manually.",
                "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await CheckAndDownloadUpdateAsync(cancellationToken, waitForStoppedPoll: manual);
            if (manual && !_updateCancellation.IsCancellationRequested)
                MessageBox.Show(this, _readyUpdateDirectory is null
                    ? "No newer published stable release is available."
                    : $"Recap {_readyUpdateVersion} is downloaded and verified. Choose Restart to update when you are ready.",
                    "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Warning($"Update preparation failed: {ex.Message}");
            if (manual) MessageBox.Show(this, "Could not download and verify an update. Check your connection and try again. Your current application is unchanged.",
                "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshUpdateIndicator()
    {
        if (Dispatcher.HasShutdownStarted || _isUpdating) return;
        UpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.IsEnabled = !_isCheckingUpdates && !_isDownloadingUpdate;
        if (_readyUpdateDirectory is not null)
        {
            UpdateButton.Content = "Restart to update";
            UpdateButton.ToolTip = $"Recap {_readyUpdateVersion} is downloaded and verified. Restart requires confirmation and waits for recording and processing to finish.";
        }
        else if (_isDownloadingUpdate)
        {
            UpdateButton.Content = $"Downloading {_updatePreparation!.Percent:0}%";
            UpdateButton.ToolTip = $"Recap {_availableUpdate?.Manifest.Version} is available. Downloading and verifying in the background; recording and processing can continue.";
        }
        else
        {
            UpdateButton.Content = _availableUpdate is null ? "Updates" : "Update available";
            UpdateButton.ToolTip = "Download and verify the available update. Restart is a separate action.";
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating || !_installTask.IsCompleted || _isCheckingUpdates || _isDownloadingUpdate) return;
        if (_readyUpdateDirectory is null)
        {
            await CheckForUpdatesAsync(true);
            return;
        }
        _installTask = InstallAvailableUpdateAsync();
        await _installTask;
    }

    private async Task InstallAvailableUpdateAsync(bool agentConfirmed = false)
    {
        var update = _availableUpdate;
        var directory = _readyUpdateDirectory;
        if (update is null || directory is null || _isUpdating)
        {
            if (agentConfirmed) throw new InvalidOperationException("Download and verify an update before requesting restart.");
            return;
        }
        if (WorkPreventsUpdate)
        {
            if (agentConfirmed) throw new InvalidOperationException("Recording or meeting work prevents restarting for an update.");
            MessageBox.Show(this, "Stop recording, wait for meeting processing to finish (or pause it in Settings), and close any meeting windows before updating.",
                "Recap is busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!agentConfirmed && MessageBox.Show(this,
            $"Restart Recap to install {update.Manifest.Version}?\n\nThe update has been downloaded and verified. The updater will verify it again before installation. Settings and recordings are preserved, and the previous executable is retained for rollback.\n\nThe executable is not Authenticode-signed; Windows may still show an unknown-publisher warning.",
            "Update and restart", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        // The confirmation pumps the dispatcher. Work may have started meanwhile.
        if (!CanRestartForUpdate) return;
        _isUpdating = true;
        IsEnabled = false;
        UpdateButton.Content = "Verifying and restarting…";
        UpdateButton.Visibility = Visibility.Visible;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            // Existing helper revalidates signature, version and file before signalling ready.
            await UpdateInstaller.LaunchHelperAsync(directory, timeout.Token);
            _handoffToUpdater = true;
            Close();
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PreparedUpdates.ClearReady();
            AppLogger.Error(ex, "Update installation failed before shutdown.");
            if (agentConfirmed) throw;
            MessageBox.Show(this, "The update could not be installed. Your existing application is unchanged. Download and verify the update again before retrying.",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isUpdating = false;
            IsEnabled = true;
            RefreshUpdateIndicator();
            if (!_handoffToUpdater) StartPendingProcessing();
        }
    }
}
