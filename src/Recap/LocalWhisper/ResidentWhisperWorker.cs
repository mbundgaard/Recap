using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;

namespace Recap.LocalWhisper;

/// <summary>Owns one local worker and its pipes. Only one request may be in flight.</summary>
internal sealed class ResidentWhisperWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly LocalWhisperDiagnostics _diagnostics;
    private readonly Task _stderrDrain;
    private readonly SafeFileHandle _job;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    private bool _failed;
    private bool _ready;
    private Process? _interpreter;
    private int _asrLoadCount;
    private int _alignmentLoadCount;
    private int _diarizationLoadCount;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    public ResidentWhisperWorker(ProcessStartInfo startInfo) : this(startInfo, LocalWhisperDiagnostics.Shared) { }

    internal ResidentWhisperWorker(ProcessStartInfo startInfo, LocalWhisperDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
        _diagnostics.Record(LocalDiagnosticEvent.ProcessStarting);
        if (startInfo.UseShellExecute || !startInfo.RedirectStandardInput
            || !startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
            throw new ArgumentException("Local worker requires redirected standard streams.", nameof(startInfo));
        _job = CreateOwnedJob();
        _process = new Process { StartInfo = startInfo };
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("Local worker could not start.");
            _diagnostics.Record(LocalDiagnosticEvent.ProcessStarted, childPid: _process.Id);
            // Windows 8+ supports nested jobs. Never run unowned if an enclosing
            // host job or security policy prevents assignment to our kill-on-close job.
            if (!AssignProcessToJobObject(_job, _process.Handle))
                throw new InvalidOperationException("Local worker ownership could not be established.");
        }
        catch
        {
            _diagnostics.Record(LocalDiagnosticEvent.ProcessFailed);
            try
            {
                if (_process.Id > 0 && !_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit();
                }
            }
            catch (Exception) { /* Do not disclose native error details. Job close also kills an assigned child. */ }
            finally { _job.Dispose(); _process.Dispose(); }
            throw new InvalidOperationException("Local worker could not start with required process ownership.");
        }
        _stderrDrain = DrainAsync(_process.StandardError);
    }

    internal int ProcessId => _process.Id;
    internal int AsrLoadCount => Volatile.Read(ref _asrLoadCount);
    internal int AlignmentLoadCount => Volatile.Read(ref _alignmentLoadCount);
    internal int DiarizationLoadCount => Volatile.Read(ref _diarizationLoadCount);

    private static SafeFileHandle CreateOwnedJob()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local worker process ownership requires Windows.");
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw Failure(); }
        var limits = new JobExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
        {
            job.Dispose();
            throw Failure();
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobIoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public JobIoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref JobExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    public async Task ExecuteAsync(string serializedRequest, Action<LocalProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A local worker request is already running.");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_failed || _process.HasExited) throw Failure();
            // A physical newline delimits each request. Never permit accidental extra requests.
            if (string.IsNullOrWhiteSpace(serializedRequest) || serializedRequest.Contains('\n') || serializedRequest.Contains('\r'))
                throw new ArgumentException("Expected one compact JSON request.", nameof(serializedRequest));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var token = linked.Token;
            var logPhase = _diagnostics.PhaseTracker(_process.Id);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!_ready) await WaitForReadyAsync(token);
                _diagnostics.Record(LocalDiagnosticEvent.RequestStarted, childPid: _process.Id);
                await _process.StandardInput.WriteLineAsync(serializedRequest.AsMemory(), token);
                await _process.StandardInput.FlushAsync(token);
                while (true)
                {
                    // This is the sole stdout reader until request completion. Cancellation is
                    // awaited before cleanup starts draining, so two readers never share a pipe.
                    var line = await _process.StandardOutput.ReadLineAsync(token);
                    if (line is null) throw Failure();
                    if (!line.StartsWith("RECAP|", StringComparison.Ordinal) || line.Length > 8192) continue;
                    using var document = JsonDocument.Parse(line.AsMemory(6));
                    var message = document.RootElement;
                    switch (message.GetProperty("kind").GetString())
                    {
                        case "done":
                            if (!message.GetProperty("ok").GetBoolean()) throw Failure();
                            token.ThrowIfCancellationRequested();
                            _diagnostics.Record(LocalDiagnosticEvent.RequestCompleted, childPid: _process.Id);
                            return;
                        case "error":
                            _diagnostics.Record(LocalDiagnosticEvent.WorkerError, childPid: _process.Id,
                                phase: message.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : "runtime_failed");
                            throw Failure();
                        case "loaded":
                            var role = message.GetProperty("role").GetString();
                            _diagnostics.Record(LocalDiagnosticEvent.ModelLoaded, childPid: _process.Id, phase: role);
                            switch (role)
                            {
                                case "asr": Interlocked.Increment(ref _asrLoadCount); break;
                                case "align": Interlocked.Increment(ref _alignmentLoadCount); break;
                                case "diarization": Interlocked.Increment(ref _diarizationLoadCount); break;
                            }
                            break;
                        case "progress":
                            var phase = message.GetProperty("phase").GetString();
                            if (phase is not ("Recognizing speech" or "Aligning words" or "Identifying speakers"
                                or "Checking transcription model" or "Checking en alignment model"
                                or "Checking da alignment model" or "Checking speaker diarization model"))
                                phase = "Local processing";
                            var percent = message.GetProperty("percent").GetDouble();
                            if (double.IsFinite(percent))
                            {
                                var update = new LocalProgress(phase, Math.Clamp(percent, 0, 100));
                                logPhase(update);
                                progress?.Invoke(update);
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _diagnostics.Record(LocalDiagnosticEvent.RequestCancelled, childPid: _process.Id);
                await TerminateAsync();
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                throw Failure();
            }
            catch
            {
                _diagnostics.Record(LocalDiagnosticEvent.RequestFailed, childPid: _process.Id);
                await TerminateAsync();
                // Never propagate protocol content, recognized text, paths or library diagnostics.
                throw Failure();
            }
        }
        finally { _gate.Release(); }
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null) throw Failure();
            if (!line.StartsWith("RECAP|", StringComparison.Ordinal) || line.Length > 8192) continue;
            using var document = JsonDocument.Parse(line.AsMemory(6));
            var message = document.RootElement;
            if (message.GetProperty("kind").GetString() != "ready") throw Failure();
            var pid = message.GetProperty("pid").GetInt32();
            if (pid <= 0) throw Failure();
            if (pid != _process.Id)
            {
                // Windows venv launchers can spawn the interpreter before launcher
                // job assignment. Bind the actual interpreter before sending any work.
                _interpreter = Process.GetProcessById(pid);
                if (!IsProcessInJob(_interpreter.Handle, _job, out var owned)) throw Failure();
                if (!owned && !AssignProcessToJobObject(_job, _interpreter.Handle)) throw Failure();
                if (!IsProcessInJob(_interpreter.Handle, _job, out owned) || !owned) throw Failure();
            }
            cancellationToken.ThrowIfCancellationRequested();
            _ready = true;
            _diagnostics.Record(LocalDiagnosticEvent.ProcessReady, childPid: pid);
            return;
        }
    }

    private static InvalidOperationException Failure() => new(
        "The local speech worker stopped or could not process this request. Retry local recognition. No audio was sent to a cloud provider.");

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer.AsMemory()) > 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // Caller owns the gate, and no request stdout read remains pending.
    private async Task TerminateAsync()
    {
        _failed = true;
        _diagnostics.Record(LocalDiagnosticEvent.Terminating, childPid: _process.Id);
        // Cancellation may precede the ready event. EOF also releases an idle
        // interpreter that escaped launcher assignment and has not reported its PID.
        try { _process.StandardInput.Close(); }
        catch (IOException) { }
        var stdoutDrain = DrainAsync(_process.StandardOutput);
        try
        {
            // Closing our sole, non-inheritable job handle terminates all members,
            // including descendants, even if the root has already exited.
            _job.Dispose();
            // Assignment itself may have failed; that interpreter is not yet
            // protected by job closure, so explicitly terminate its tree as well.
            if (_interpreter is not null && !_interpreter.HasExited)
                _interpreter.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None);
            _diagnostics.Record(LocalDiagnosticEvent.ProcessExited, childPid: _process.Id, exitCode: _process.ExitCode);
            if (_interpreter is not null)
            {
                await _interpreter.WaitForExitAsync(CancellationToken.None);
                _diagnostics.Record(LocalDiagnosticEvent.ProcessExited, childPid: _interpreter.Id, exitCode: _interpreter.ExitCode);
            }
        }
        catch (InvalidOperationException) { }
        finally { await Task.WhenAll(stdoutDrain, _stderrDrain); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _diagnostics.Record(LocalDiagnosticEvent.UnloadStarted, childPid: _process.Id);
        // An in-flight request must not make disposal wait indefinitely.
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (!_failed)
            {
                // EOF asks a healthy resident loop to exit; stdout must drain while waiting.
                var stdoutDrain = DrainAsync(_process.StandardOutput);
                try
                {
                    try { _process.StandardInput.Close(); }
                    catch (IOException) { }
                    using var timeout = new CancellationTokenSource(ShutdownTimeout);
                    try { await _process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        _diagnostics.Record(LocalDiagnosticEvent.Terminating, childPid: _process.Id);
                        _job.Dispose();
                        await _process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                finally
                {
                    if (_process.HasExited)
                        _diagnostics.Record(LocalDiagnosticEvent.ProcessExited, childPid: _process.Id, exitCode: _process.ExitCode);
                    // A child may inherit pipes even after the resident exits normally.
                    _job.Dispose();
                    await Task.WhenAll(stdoutDrain, _stderrDrain);
                }
            }
        }
        finally
        {
            _job.Dispose();
            _diagnostics.Record(LocalDiagnosticEvent.UnloadCompleted, childPid: _process.Id);
            _interpreter?.Dispose();
            _process.Dispose();
            _lifetime.Dispose();
            _gate.Release();
        }
    }
}
