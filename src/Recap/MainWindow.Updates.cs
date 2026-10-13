using System.Windows;
using Recap.Logging;
using Recap.Updates;

namespace Recap;

public partial class MainWindow
{
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly GitHubUpdateService _updates = new();
    private AvailableUpdate? _availableUpdate;
    private bool _isCheckingUpdates;
    private bool _isUpdating;
    private bool _handoffToUpdater;
    private bool _cancelingUpdateShutdown;
    private Task _installTask = Task.CompletedTask;

    private bool WorkPreventsUpdate => _session is not null || _recordingStarted is not null
        || !_processingTask.IsCompleted || _isPausingProcessing || OwnedWindows.Count > 0;

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_isCheckingUpdates || _isUpdating || _updateCancellation.IsCancellationRequested) return;
        if (!UpdateTrust.SupportsInstallation)
        {
            if (manual) MessageBox.Show(this, "In-app updates are available in published Windows x64 single-file releases. Development builds and ARM64 builds must be updated manually.",
                "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _isCheckingUpdates = true;
        UpdateButton.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            _availableUpdate = await _updates.CheckAsync(timeout.Token);
            UpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateButton.Content = _availableUpdate is null ? "Updates" : $"Update to {_availableUpdate.Manifest.Version}";
            if (manual)
            {
                if (_availableUpdate is null)
                    MessageBox.Show(this, "No newer published stable release is available.", "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                {
                    _installTask = InstallAvailableUpdateAsync();
                    await _installTask;
                }
            }
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Warning($"Update check failed: {ex.Message}");
            if (manual) MessageBox.Show(this, "Could not check for a verified update. Check your connection and try again.\n\n" + ex.Message,
                "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isCheckingUpdates = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating || !_installTask.IsCompleted) return;
        _installTask = InstallAvailableUpdateAsync();
        await _installTask;
    }

    private async Task InstallAvailableUpdateAsync()
    {
        var update = _availableUpdate;
        if (update is null || _isUpdating) return;
        if (WorkPreventsUpdate)
        {
            MessageBox.Show(this, "Stop recording, wait for meeting processing to finish (or pause it in Settings), and close any meeting windows before updating.",
                "Recap is busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
            $"Download Recap {update.Manifest.Version} and restart?\n\nThe download will be verified against Recap's signed update manifest. Settings and recordings are preserved. The previous executable is retained for rollback.\n\nThe executable is not Authenticode-signed; Windows may still show an unknown-publisher warning.",
            "Update and restart", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (WorkPreventsUpdate || _updateCancellation.IsCancellationRequested) return;
        _isUpdating = true;
        IsEnabled = false;
        UpdateButton.Content = "Downloading…";
        UpdateButton.Visibility = Visibility.Visible;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var progress = new Progress<double>(percent =>
            {
                if (_isUpdating) UpdateButton.Content = $"Downloading {percent:0}%";
            });
            var directory = await _updates.DownloadAsync(update, progress, timeout.Token);
            UpdateButton.Content = "Verifying and restarting…";
            await UpdateInstaller.LaunchHelperAsync(directory, timeout.Token);
            _handoffToUpdater = true;
            Close();
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Update installation failed before shutdown.");
            MessageBox.Show(this, "The update could not be installed. Your existing application is unchanged.\n\n" + ex.Message,
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isUpdating = false;
            IsEnabled = true;
            UpdateButton.Content = $"Update to {update.Manifest.Version}";
            if (!_handoffToUpdater) StartPendingProcessing();
        }
    }
}
