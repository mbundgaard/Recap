using Recap.Updates;

namespace Recap;

/// <summary>
/// Check and prepare only: no installation, process launch or UI blocking. The
/// download delegate must return only after verifying the signed package.
/// Call from the owning dispatcher; the async flow retains its synchronization context.
/// </summary>
internal sealed class UpdatePreparation(
    Func<CancellationToken, Task<AvailableUpdate?>> check,
    Func<AvailableUpdate, IProgress<double>, CancellationToken, Task<string>> download)
{
    private Task _active = Task.CompletedTask;
    public AvailableUpdate? Available { get; private set; }
    public bool Checking { get; private set; }
    public bool Downloading { get; private set; }
    public string? ReadyDirectory { get; private set; }
    public string? ReadyVersion { get; private set; }
    public double Percent { get; private set; }
    public event Action? Changed;

    public Task CheckAndDownloadAsync(CancellationToken token)
    {
        if (!_active.IsCompleted) return _active;
        token.ThrowIfCancellationRequested();
        if (ReadyDirectory is not null) return Task.CompletedTask;
        // Set the task before notifications can re-enter via a dispatcher subscriber.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _active = completion.Task;
        _ = PrepareAsync(token, completion);
        return _active;
    }

    private async Task PrepareAsync(CancellationToken token, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            Checking = true;
            Percent = 0;
            Changed?.Invoke();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                Available = await check(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
            }
            token.ThrowIfCancellationRequested();
            Checking = false;
            if (Available is not null)
            {
                var update = Available;
                Downloading = true;
                Changed?.Invoke();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                // Progress can arrive after cancellation: don't update a subsequent operation.
                var progress = new Progress<double>(percent =>
                {
                    if (ReferenceEquals(_active, completion.Task) && Downloading && !token.IsCancellationRequested)
                    {
                        Percent = Math.Clamp(percent, 0, 100);
                        Changed?.Invoke();
                    }
                });
                var directory = await download(update, progress, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(directory))
                    throw new InvalidOperationException("The verified update staging directory is missing.");
                ReadyDirectory = directory;
                ReadyVersion = update.Manifest.Version;
                Percent = 100;
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Checking = false;
            Downloading = false;
            try { Changed?.Invoke(); }
            finally
            {
                if (failure is OperationCanceledException cancelled)
                    completion.TrySetCanceled(cancelled.CancellationToken);
                else if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult();
            }
        }
    }

    public void ClearReady()
    {
        ReadyDirectory = null;
        ReadyVersion = null;
        Changed?.Invoke();
    }
}
