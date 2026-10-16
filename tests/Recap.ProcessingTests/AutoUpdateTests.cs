using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Recap;
using Recap.Settings;
using Recap.Updates;

/// <summary>Injected state-machine delegates only; never contacts a release host or invokes installation.</summary>
internal static class AutoUpdateTests
{
    internal static void Check(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var preparationField = typeof(MainWindow).GetField("_updatePreparation", flags)!;
        var updatingField = typeof(MainWindow).GetField("_isUpdating", flags)!;
        var startedField = typeof(MainWindow).GetField("_recordingStarted", flags)!;
        var processingField = typeof(MainWindow).GetField("_processingTask", flags)!;
        var cancellationField = typeof(MainWindow).GetField("_updatePollingCancellation", flags)!;
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", flags)!.GetValue(window)!;
        var workGuard = typeof(MainWindow).GetProperty("WorkPreventsUpdate", flags)!;
        var oldPreparation = preparationField.GetValue(window);
        var oldStarted = startedField.GetValue(window);
        var oldProcessing = processingField.GetValue(window);
        var oldCancellation = cancellationField.GetValue(window);
        var oldOptIn = settings.CheckForUpdatesOnStartup;
        var button = (Button)window.FindName("UpdateButton");
        var oldButtonVisibility = button.Visibility;
        var oldButtonContent = button.Content;
        var oldButtonEnabled = button.IsEnabled;
        var oldButtonTooltip = button.ToolTip;
        var update = Candidate();
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var optOut = new CancellationTokenSource();
        var pendingWork = new List<Task>();
        try
        {
            var checks = 0;
            var downloads = 0;
            var entered = Completion();
            var verified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            IProgress<double>? reportedProgress = null;
            var preparation = new UpdatePreparation(
                _ => { checks++; return Task.FromResult<AvailableUpdate?>(update); },
                (_, progress, token) =>
                {
                    downloads++;
                    reportedProgress = progress;
                    entered.TrySetResult();
                    return verified.Task.WaitAsync(token);
                });
            preparationField.SetValue(window, preparation);
            preparation.Changed += () => Invoke(window, "RefreshUpdateIndicator");
            // Recording must not block background checking/downloading, but must block restart.
            startedField.SetValue(window, DateTime.Now);
            var first = BeginPreparation(window, deadline.Token);
            var duplicate = BeginPreparation(window, deadline.Token);
            pendingWork.AddRange([first, duplicate]);
            Await(entered.Task, window);
            Assert(checks == 1 && downloads == 1, "Repeated checks share one check/download flight");
            Assert(preparation.Downloading && preparation.ReadyDirectory is null, "Candidate discovery does not mean verified readiness");
            reportedProgress!.Report(100);
            Pump(window);
            Assert(preparation.Percent == 100, "Synthetic transfer reached 100 before verification gate opens");
            Invoke(window, "RefreshUpdateIndicator");
            Assert(preparation.ReadyDirectory is null && !IsRestart(button), "Download progress reaching 100 cannot expose restart before verification returns");
            Assert(!(bool)updatingField.GetValue(window)! && window.IsEnabled,
                "Background preparation does not enter install mode or disable recorder");
            Assert((bool)workGuard.GetValue(window)!, "Active recording blocks restart while download continues");
            verified.TrySetResult("synthetic-verified-staging-directory");
            Await(Task.WhenAll(first, duplicate), window);
            Invoke(window, "RefreshUpdateIndicator");
            Assert(preparation.ReadyDirectory == "synthetic-verified-staging-directory" && preparation.ReadyVersion == update.Manifest.Version,
                "Ready state published only after verified download delegate succeeds");
            Assert(IsRestart(button) && button.Visibility == Visibility.Visible, "Verified package exposes Restart to update indicator");
            Assert(!(bool)updatingField.GetValue(window)! && window.IsEnabled, "Ready package never automatically starts install/restart");
            Assert((bool)workGuard.GetValue(window)!, "Ready update remains protected by active-recording restart guard");
            Assert(!CanRestart(window), "Ready click eligibility blocks recording without opening a modal");
            Await(preparation.CheckAndDownloadAsync(deadline.Token), window);
            Assert(checks == 1 && downloads == 1, "Repeated checks do not redownload an already verified package");
            startedField.SetValue(window, null);
            processingField.SetValue(window, new TaskCompletionSource().Task);
            Assert((bool)workGuard.GetValue(window)! && !CanRestart(window), "Meeting processing also blocks ready-update restart");
            processingField.SetValue(window, oldProcessing);
            Assert(CanRestart(window), "Idle verified update becomes eligible for explicit user restart only");

            CheckFailureRetry(update, window, deadline.Token);
            CheckCancellationAfterDownload(update, window);
            var noUpdate = new UpdatePreparation(_ => Task.FromResult<AvailableUpdate?>(null),
                (_, _, _) => throw new Exception("No-update result must not download"));
            Await(noUpdate.CheckAndDownloadAsync(deadline.Token), window);
            Assert(noUpdate.ReadyDirectory is null && !noUpdate.Checking && !noUpdate.Downloading,
                "No newer release leaves updater idle and not ready");
            Assert((TimeSpan)typeof(MainWindow).GetField("UpdatePollInterval", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
                == TimeSpan.FromHours(4), "Automatic polling uses a four-hour interval");

            var cancellationObserved = Completion();
            var cancellationEntered = Completion();
            var cancelling = new UpdatePreparation(_ => Task.FromResult<AvailableUpdate?>(update), async (_, _, token) =>
            {
                cancellationEntered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancellationObserved.TrySetResult(); throw; }
                return "unreachable";
            });
            preparationField.SetValue(window, cancelling);
            cancellationField.SetValue(window, optOut);
            settings.CheckForUpdatesOnStartup = true;
            var pending = BeginPreparation(window, optOut.Token);
            pendingWork.Add(pending);
            Await(cancellationEntered.Task, window);
            settings.CheckForUpdatesOnStartup = false;
            Invoke(window, "ConfigureUpdatePolling");
            Assert(optOut.IsCancellationRequested, "Opting out cancels the polling/preparation scope");
            Await(cancellationObserved.Task, window);
            try { Await(pending, window); } catch (OperationCanceledException) { }
            Assert(cancelling.ReadyDirectory is null && !cancelling.Downloading, "Cancelled preparation never becomes ready");
            Assert(!(bool)updatingField.GetValue(window)!, "Opt-out cancellation never enters installer mode");
            Console.WriteLine("PASS: mocked update preparation single-flight, verified-ready gating, background recorder availability, retry/cancellation/opt-out and busy restart guards; no install or network.");
        }
        finally
        {
            deadline.Cancel();
            optOut.Cancel();
            foreach (var pending in pendingWork)
                try { Await(pending, window); } catch (Exception) { }
            preparationField.SetValue(window, oldPreparation);
            startedField.SetValue(window, oldStarted);
            processingField.SetValue(window, oldProcessing);
            cancellationField.SetValue(window, oldCancellation);
            settings.CheckForUpdatesOnStartup = oldOptIn;
            button.Visibility = oldButtonVisibility;
            button.Content = oldButtonContent;
            button.IsEnabled = oldButtonEnabled;
            button.ToolTip = oldButtonTooltip;
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }

    private static void CheckFailureRetry(AvailableUpdate update, MainWindow window, CancellationToken token)
    {
        var checks = 0;
        var downloads = 0;
        var preparation = new UpdatePreparation(_ => { checks++; return Task.FromResult<AvailableUpdate?>(update); },
            (_, _, _) => ++downloads == 1
                ? Task.FromException<string>(new InvalidDataException("Synthetic verification failure"))
                : Task.FromResult("verified-on-retry"));
        try { Await(preparation.CheckAndDownloadAsync(token), window); throw new Exception("Expected verification failure"); }
        catch (InvalidDataException) { }
        Assert(preparation.ReadyDirectory is null && !preparation.Downloading && !preparation.Checking,
            "Verification failure clears busy state without publishing readiness");
        Await(preparation.CheckAndDownloadAsync(token), window);
        Assert(downloads == 2 && checks == 2 && preparation.ReadyDirectory == "verified-on-retry", "Next check retries after failed verification");
        preparation.ClearReady();
        Assert(preparation.ReadyDirectory is null, "Failed installation can invalidate prepared readiness");
    }

    private static void CheckCancellationAfterDownload(AvailableUpdate update, MainWindow window)
    {
        using var cancellation = new CancellationTokenSource();
        var preparation = new UpdatePreparation(_ => Task.FromResult<AvailableUpdate?>(update), (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult("must-not-be-published");
        });
        try { Await(preparation.CheckAndDownloadAsync(cancellation.Token), window); }
        catch (OperationCanceledException) { }
        Assert(preparation.ReadyDirectory is null, "Cancellation winning download-completion race prevents readiness");
    }

    // These dummy manifests never enter production verification/installer paths.
    // Existing UpdaterTests separately exercise real signatures, hashes and rollback.
    private static AvailableUpdate Candidate() => new(new UpdateManifest(1, "9.1.0", "win-x64",
        UpdateTrust.ExecutableName, new string('0', 64), 1), [], []);
    private static Task BeginPreparation(MainWindow window, CancellationToken token) => (Task)typeof(MainWindow)
        .GetMethod("CheckAndDownloadUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [token, true])!;
    private static bool CanRestart(MainWindow window) => (bool)typeof(MainWindow)
        .GetProperty("CanRestartForUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Pump(MainWindow window)
    {
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool IsRestart(Button button) => button.Content?.ToString()?.Contains("Restart", StringComparison.OrdinalIgnoreCase) == true;
    private static void Invoke(MainWindow window, string name)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("Missing update test seam: " + name);
        try { method.Invoke(window, null); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static void Await(Task task, MainWindow window)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < timeout)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new Exception("Mock update test timed out");
        task.GetAwaiter().GetResult();
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Automatic updates: " + message); }
}
