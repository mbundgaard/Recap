using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Recap.Storage;

namespace Recap.LocalWhisper;

public sealed record LocalProgress(string Message, double Percent);
public sealed record LocalLanguageStatus(string Language, string Model, bool CpuReady, bool CudaReady);
public sealed record LocalRuntimeStatus(string Folder, bool SetupRunning, string Message, double Percent,
    bool CpuReady, bool CudaReady, string EngineVersion);

public static class LocalWhisperRuntime
{
    public const string EngineVersion = "WhisperX 3.8.6 / community-1";
    private const string UvUrl = "https://files.pythonhosted.org/packages/bc/37/e15a46f880b2c230f7d6934f046825ebe547f90008984c861e84e2ef34c4/uv-0.9.15-py3-none-win_amd64.whl";
    private const string UvHash = "e46f36c80353fb406d4c0fb2cfe1953b4a01bfb3fc5b88dd4f763641b8899f1a";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim AutomaticSetupGate = new(1, 1);
    private static readonly SemaphoreSlim ResidentGate = new(1, 1);
    private static ResidentWhisperWorker? _residentWorker;
    private static FileStream? _residentLease;
    private static LocalWhisperSettings? _residentSettings;
    private static string? _residentKey;
    private static int _processingBatches;

    public static bool WorkerResident => Volatile.Read(ref _residentWorker) is not null;

    // A queue owns a scope; no models/processes are started until its first local request.
    public static IAsyncDisposable BeginProcessingBatch()
    {
        lock (Gate) _processingBatches++;
        return new ProcessingBatch();
    }

    private sealed class ProcessingBatch : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await ResidentGate.WaitAsync();
            try
            {
                bool idle;
                lock (Gate) idle = --_processingBatches == 0;
                if (idle) await ReleaseResidentCoreAsync();
            }
            finally { ResidentGate.Release(); }
        }
    }

    public static async Task UnloadAsync()
    {
        await ResidentGate.WaitAsync();
        try { await ReleaseResidentCoreAsync(); }
        finally { ResidentGate.Release(); }
    }

    private static async Task ReleaseResidentCoreAsync()
    {
        try
        {
            if (_residentWorker is not null)
            {
                LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.UnloadStarted, _residentSettings);
                await _residentWorker.DisposeAsync();
                LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.UnloadCompleted, _residentSettings);
            }
        }
        finally
        {
            _residentWorker = null;
            _residentSettings = null;
            _residentKey = null;
            _residentLease?.Dispose();
            _residentLease = null;
        }
    }
    private static CancellationTokenSource? _setupCancellation;
    private static LocalProgress _progress = new("Not checked", 0);
    public static bool SetupRunning { get { lock (Gate) return _setupCancellation is not null; } }
    public static void CancelSetup() { lock (Gate) _setupCancellation?.Cancel(); }
    public static bool Supported => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    private static byte[] Resource(string name)
    {
        using var stream = typeof(LocalWhisperRuntime).Assembly.GetManifestResourceStream("Recap.LocalWhisper." + name)
            ?? throw new InvalidOperationException("Local Whisper resources are missing.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    private static string ResourceText(string name) => Encoding.UTF8.GetString(Resource(name)).Replace("\r\n", "\n");
    public static string EngineKey(string backend) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        ResourceText("requirements-" + backend + ".txt") + ResourceText("worker.py") + ResourceText("community-1.json"))))[..16];
    private static string EnvironmentFolder(string backend) => Path.Combine(LocalWhisperSettings.Root, "runtime", backend + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ResourceText("requirements-" + backend + ".txt"))))[..16]);
    public static string PythonPath(string backend) => Path.Combine(EnvironmentFolder(backend), "Scripts", "python.exe");
    private static string Receipt(LocalWhisperSettings settings, string backend) => Path.Combine(LocalWhisperSettings.Root, "ready",
        $"{settings.Model}-{backend}-{settings.Diarization}.json");

    public static bool IsReady(LocalWhisperSettings settings, string backend)
    {
        settings.Validate();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Receipt(settings, backend)));
            if (!File.Exists(PythonPath(backend)) || document.RootElement.GetProperty("engineKey").GetString() != EngineKey(backend)) return false;
            var files = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
            return files.Length > 0 && files.All(file => ModelFileExists(file));
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static bool ModelFileExists(JsonElement file)
    {
        var root = Path.GetFullPath(Path.Combine(LocalWhisperSettings.Root, "models")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(LocalWhisperSettings.Root, file.GetProperty("path").GetString() ?? ""));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            && new FileInfo(path).Length == file.GetProperty("size").GetInt64();
    }

    public static LocalRuntimeStatus Status(LocalWhisperSettings settings)
    {
        settings.Validate();
        lock (Gate) return new(LocalWhisperSettings.Root, _setupCancellation is not null, _progress.Message, _progress.Percent,
            IsReady(settings, "cpu"), IsReady(settings, "cuda"), EngineVersion);
    }

    public static IReadOnlyList<LocalLanguageStatus> StatusSelected(LocalWhisperSettings settings) =>
        new[] { "en-US", "da-DK" }.Select(language =>
        {
            var selected = settings.ForLanguage(language);
            return new LocalLanguageStatus(language, selected.Model, IsReady(selected, "cpu"), IsReady(selected, "cuda"));
        }).ToArray();

    public static Task SetupSelectedAsync(LocalWhisperSettings selected, bool download,
        IProgress<LocalProgress>? progress = null, CancellationToken cancellation = default) =>
        SetupManyAsync(new[] { selected.ForLanguage("en-US"), selected.ForLanguage("da-DK") }
            .DistinctBy(settings => settings.Model).ToArray(), download, progress, cancellation);

    public static Task SetupAsync(LocalWhisperSettings selected, bool download,
        IProgress<LocalProgress>? progress = null, CancellationToken cancellation = default) =>
        SetupManyAsync([selected.Copy()], download, progress, cancellation);

    private static async Task SetupManyAsync(IReadOnlyList<LocalWhisperSettings> selections, bool download,
        IProgress<LocalProgress>? progress, CancellationToken cancellation)
    {
        foreach (var selected in selections)
        {
            selected.Validate();
            LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.SetupRequested, selected);
        }
        if (!Supported) throw new InvalidOperationException("Local Whisper currently requires Windows x64.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock (Gate)
        {
            if (_setupCancellation is not null) throw new InvalidOperationException("A local setup or verification is already running.");
            _setupCancellation = linked;
        }
        var logPhase = LocalWhisperDiagnostics.Shared.PhaseTracker();
        void Report(LocalProgress value) { logPhase(value); lock (Gate) _progress = value; progress?.Report(value); }
        var entered = false;
        try
        {
            await ResidentGate.WaitAsync(linked.Token);
            entered = true;
            await ReleaseResidentCoreAsync();
            Report(new("Waiting for the local processor", 0));
            using var lease = await AcquireAsync(linked.Token);
            var installedBackends = new HashSet<string>();
            foreach (var selected in selections)
            {
                linked.Token.ThrowIfCancellationRequested();
                var settings = await HardwareDetection.ResolveAsync(selected, linked.Token);
                LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.SetupResolved, settings);
                var backend = settings.Device;
                var receipt = Receipt(settings, backend);
                if (File.Exists(receipt)) File.Delete(receipt); // A failed verification must not leave a stale ready flag.
                var root = LocalWhisperSettings.Root;
                var script = InstallWorker();
                if (download && installedBackends.Add(backend))
                {
                    Report(new("Downloading the isolated Python runtime and dependencies", 0));
                    var drive = new DriveInfo(Path.GetPathRoot(root)!);
                    if (drive.AvailableFreeSpace < (backend == "cuda" ? 16L : 10L) * 1024 * 1024 * 1024)
                        throw new InvalidOperationException("Free at least 10 GB for CPU setup or 16 GB for GPU setup, including download caches.");
                    var uv = await InstallUvAsync(linked.Token);
                    if (!File.Exists(PythonPath(backend)))
                        await RunAsync(uv, ["--no-config", "venv", "--managed-python", "--python", "3.11.13", EnvironmentFolder(backend)], null, null, linked.Token);
                    var requirements = Path.Combine(root, "runtime", "requirements-" + backend + ".txt");
                    await File.WriteAllBytesAsync(requirements, Resource("requirements-" + backend + ".txt"), linked.Token);
                    await RunAsync(uv, ["--no-config", "pip", "sync", "--python", PythonPath(backend), "--torch-backend", backend == "cuda" ? "cu128" : "cpu",
                    "--default-index", "https://pypi.org/simple", "--require-hashes", "--only-binary", ":all:", requirements], null, null, linked.Token);
                }
                if (download)
                {
                    Report(new("Preparing speaker model files from GitHub", 0));
                    await CommunityModelDownload.EnsureAsync(linked.Token);
                    await WorkerAsync(backend, script, new { mode = "setup", root, settings = settings.WorkerSettings() }, Report, linked.Token);
                }
                if (!File.Exists(PythonPath(backend))) throw new InvalidOperationException("Install the selected local runtime before verifying.");
                // Verification is a separate process with networking disabled.
                Report(new("Verifying the downloaded models with networking disabled", 0));
                await WorkerAsync(backend, script, new { mode = "verify", root, settings = settings.WorkerSettings() }, Report, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(root, "ready"));
                using var verification = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "worker", "verified-files.json"), linked.Token));
                var files = verification.RootElement.GetProperty("files").Clone();
                if (!files.EnumerateArray().Any() || !files.EnumerateArray().All(ModelFileExists))
                    throw new InvalidDataException("The offline verification did not report a complete local model installation.");
                AtomicFile.WriteText(Receipt(settings, backend), JsonSerializer.Serialize(new { engineKey = EngineKey(backend), verifiedUtc = DateTime.UtcNow, settings, files }, Json));
            }
            LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.SetupCompleted);
            Report(new("Ready. Selected models loaded successfully with networking disabled.", 100));
        }
        catch (OperationCanceledException) { LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.SetupCancelled); Report(new("Setup cancelled. Completed downloads are kept for retry.", 0)); throw; }
        catch (Exception ex) { LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.SetupFailed); Report(new("Setup or verification failed: " + ex.Message, 0)); throw; }
        finally
        {
            if (entered) ResidentGate.Release();
            lock (Gate) _setupCancellation = null;
        }
    }

    public static async Task TranscribeAsync(LocalWhisperSettings selected, string audio, string track, string language,
        string prefix, string result, IProgress<LocalProgress> progress, CancellationToken cancellation)
    {
        if (!Supported) throw new InvalidOperationException("Local Whisper currently requires Windows x64.");
        selected.Validate();
        LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.RecognitionRequested, selected);
        var key = JsonSerializer.Serialize(selected.WorkerSettings(), Json);
        var prepared = false;
        while (true)
        {
            progress.Report(new("Waiting for the local processor", 0));
            LocalWhisperSettings settings;
            await ResidentGate.WaitAsync(cancellation);
            try
            {
                // Reuse the resolved device while resident: our own GPU allocation must not
                // cause Auto detection to select a different backend on the next track.
                if (_residentKey != key) await ReleaseResidentCoreAsync();
                _residentLease ??= await AcquireAsync(cancellation);
                settings = _residentSettings?.Copy() ?? await HardwareDetection.ResolveAsync(selected, cancellation);
                if (selected.Device == "auto" && settings.Device == "cuda" && !IsReady(settings, "cuda") && IsReady(settings, "cpu"))
                {
                    settings.Device = "cpu";
                    settings.ComputeType = selected.ComputeType == "auto" ? "int8" : selected.ComputeType;
                    settings.Validate();
                }
                LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.RecognitionResolved, settings);
                if (IsReady(settings, settings.Device))
                {
                    _residentSettings = settings.Copy();
                    _residentKey = key;
                    _residentWorker ??= new ResidentWhisperWorker(CreateStartInfo(PythonPath(settings.Device),
                        ["-I", "-X", "pycache_prefix=" + Path.Combine(LocalWhisperSettings.Root, "cache", "python"), InstallWorker(), "--resident"]));
                    var request = JsonSerializer.Serialize(new
                    {
                        mode = "transcribe", root = LocalWhisperSettings.Root, settings = settings.WorkerSettings(),
                        engineKey = EngineKey(settings.Device), audio, track, language, outputPrefix = prefix, result
                    }, Json);
                    await _residentWorker.ExecuteAsync(request, progress.Report, cancellation);
                    LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.RecognitionCompleted, settings);
                    bool idle;
                    lock (Gate) idle = _processingBatches == 0;
                    if (idle) await ReleaseResidentCoreAsync(); // Standalone calls have no owning queue.
                    return;
                }
                await ReleaseResidentCoreAsync();
                if (prepared) throw new InvalidOperationException("Local model installation changed after setup; retry processing.");
            }
            catch (OperationCanceledException)
            {
                LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.RecognitionCancelled);
                await ReleaseResidentCoreAsync();
                throw;
            }
            catch
            {
                LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.RecognitionFailed);
                await ReleaseResidentCoreAsync();
                throw;
            }
            finally { ResidentGate.Release(); }

            // Setup owns the same locks; release request ownership before preparing assets.
            await AutomaticSetupGate.WaitAsync(cancellation);
            try
            {
                while (SetupRunning) await Task.Delay(250, cancellation);
                if (!IsReady(settings, settings.Device))
                {
                    progress.Report(new("Downloading missing local runtime and models", 0));
                    await SetupAsync(settings, true, progress, cancellation);
                }
                prepared = true;
            }
            finally { AutomaticSetupGate.Release(); }
        }
    }

    private static string InstallWorker()
    {
        var folder = Path.Combine(LocalWhisperSettings.Root, "worker"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "worker.py");
        AtomicFile.WriteText(path, Encoding.UTF8.GetString(Resource("worker.py")));
        return path;
    }
    private static async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        Directory.CreateDirectory(LocalWhisperSettings.Root);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(LocalWhisperSettings.Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(250, token); }
        }
    }
    private static Task WorkerAsync(string backend, string script, object request, Action<LocalProgress>? progress, CancellationToken token) =>
        RunAsync(PythonPath(backend), ["-I", "-X", "pycache_prefix=" + Path.Combine(LocalWhisperSettings.Root, "cache", "python"), script],
            JsonSerializer.Serialize(request, Json), progress, token);

    private static ProcessStartInfo CreateStartInfo(string executable, string[] arguments)
    {
        var root = LocalWhisperSettings.Root;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("UV_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("PIP_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("HF_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("HUGGING", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (var (key, path) in new[]
        {
            ("UV_CACHE_DIR", "cache/uv"), ("UV_PYTHON_INSTALL_DIR", "python"), ("UV_PYTHON_BIN_DIR", "bin"),
            ("UV_TOOL_DIR", "tools"), ("UV_TOOL_BIN_DIR", "bin"), ("TEMP", "tmp"), ("TMP", "tmp")
        })
        {
            var directory = Path.Combine(root, path); Directory.CreateDirectory(directory); start.Environment[key] = directory;
        }
        start.Environment["UV_NO_PROGRESS"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";
        return start;
    }

    private static async Task RunAsync(string executable, string[] arguments, string? input, Action<LocalProgress>? progress, CancellationToken token)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments) };
        LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.ProcessStarting);
        process.Start();
        LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.ProcessStarted, childPid: process.Id);
        var logPhase = LocalWhisperDiagnostics.Shared.PhaseTracker(process.Id);
        var errorCode = "runtime_failed";
        var done = false;
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("RECAP|", StringComparison.Ordinal) || line.Length > 8192) continue;
                try
                {
                    using var document = JsonDocument.Parse(line[6..]);
                    var message = document.RootElement;
                    switch (message.GetProperty("kind").GetString())
                    {
                        case "done": done = message.GetProperty("ok").GetBoolean(); break;
                        case "error":
                            errorCode = message.GetProperty("code").GetString() ?? "runtime_failed";
                            LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.WorkerError, childPid: process.Id, phase: errorCode);
                            break;
                        case "progress":
                            var phase = message.GetProperty("phase").GetString();
                            if (phase is not ("Recognizing speech" or "Aligning words" or "Identifying speakers" or "Checking transcription model"
                                or "Checking en alignment model" or "Checking da alignment model" or "Checking speaker diarization model")) phase = "Local processing";
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
                catch (JsonException) { }
            }
        });
        var stderr = Task.Run(async () =>
        {
            var buffer = new char[4096];
            while (await process.StandardError.ReadAsync(buffer) > 0) { } // Never retain third-party text or credential errors.
        });
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || input is not null && !done)
                throw new InvalidOperationException(ErrorMessage(errorCode, input is null));
        }
        finally
        {
            if (!process.HasExited)
            {
                LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.Terminating, childPid: process.Id);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            LocalWhisperDiagnostics.Shared.Record(LocalDiagnosticEvent.ProcessExited, childPid: process.Id, exitCode: process.ExitCode);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static string ErrorMessage(string code, bool installer) => code switch
    {
        "access_denied" => "A model host denied access. Check your connection and model availability. Speaker models are downloaded from Recap's GitHub repository without a token.",
        "cuda_unavailable" => "CUDA is not available in the selected runtime/device. Check your NVIDIA driver or select CPU and run setup again.",
        "compute_unsupported" => "The selected precision is not supported on this device. Use Detect best settings or choose a supported precision.",
        "out_of_memory" => "The local worker ran out of memory. Reduce batch size, choose int8 precision, or select a smaller model.",
        _ => installer ? "The isolated Python dependency installation failed. Check your connection, available disk space and Windows x64 support, then retry. No system Python was changed."
            : "The local worker could not load or process the selected models. Download/verify the selected setup, check memory and GPU compatibility, then retry. No audio was sent to a cloud provider."
    };

    private static async Task<string> InstallUvAsync(CancellationToken token)
    {
        var directory = Path.Combine(LocalWhisperSettings.Root, "tools"); Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "uv-0.9.15.exe");
        // Always verify the archive before extracting an executable, including retries.
        var archive = Path.Combine(directory, "uv-0.9.15.whl");
        if (!File.Exists(archive) || !await MatchesHash(archive, token))
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await client.GetAsync(UvUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var temporary = archive + ".tmp";
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long bytes = 0; int count;
                    while ((count = await source.ReadAsync(buffer, token)) > 0)
                    {
                        bytes += count;
                        if (bytes > 50 * 1024 * 1024) throw new InvalidDataException("Runtime bootstrap download exceeds its limit.");
                        await target.WriteAsync(buffer.AsMemory(0, count), token);
                    }
                }
                if (!await MatchesHash(temporary, token)) throw new InvalidDataException("Runtime bootstrap checksum did not match.");
                File.Move(temporary, archive, true);
            }
            finally { File.Delete(temporary); }
        }
        using var zip = ZipFile.OpenRead(archive);
        zip.GetEntry("uv-0.9.15.data/scripts/uv.exe")!.ExtractToFile(executable, overwrite: true);
        foreach (var name in new[] { "LICENSE-MIT", "LICENSE-APACHE" })
            zip.GetEntry("uv-0.9.15.dist-info/licenses/" + name)!.ExtractToFile(Path.Combine(directory, "uv-" + name + ".txt"), overwrite: true);
        return executable;
    }
    private static async Task<bool> MatchesHash(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(UvHash, StringComparison.OrdinalIgnoreCase);
    }
}
