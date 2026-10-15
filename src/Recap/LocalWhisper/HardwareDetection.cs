using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Recap.LocalWhisper;

public sealed record NvidiaGpu(int Index, string Name, double TotalGb, double FreeGb, double ComputeCapability, string Driver);
public sealed record LocalHardware(int CpuThreads, double TotalRamGb, double AvailableRamGb, IReadOnlyList<NvidiaGpu> Gpus);
public sealed record LocalRecommendation(LocalWhisperSettings Settings, LocalHardware Hardware, IReadOnlyList<string> Reasons);

public static class HardwareDetection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    public static async Task<LocalHardware> DetectAsync(CancellationToken token = default)
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        var gpus = new List<NvidiaGpu>();
        var executable = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        }.FirstOrDefault(File.Exists);
        if (executable is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--query-gpu=index,name,memory.total,memory.free,compute_cap,driver_version");
            start.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                if (process.ExitCode == 0)
                    foreach (var line in (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var fields = line.Trim().Split(',').Select(x => x.Trim()).ToArray();
                        if (fields.Length == 6 && int.TryParse(fields[0], out var index)
                            && double.TryParse(fields[2], CultureInfo.InvariantCulture, out var total)
                            && double.TryParse(fields[3], CultureInfo.InvariantCulture, out var free)
                            && double.TryParse(fields[4], CultureInfo.InvariantCulture, out var capability))
                            gpus.Add(new(index, fields[1], total / 1024, free / 1024, capability, fields[5]));
                    }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
                catch (InvalidOperationException) { }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(Environment.ProcessorCount, memory.TotalPhysical / 1073741824d, memory.AvailablePhysical / 1073741824d, gpus);
    }

    public static LocalRecommendation Recommend(LocalWhisperSettings selected, LocalHardware hardware)
    {
        selected.Validate();
        var result = selected.Copy();
        var model = LocalWhisperSettings.Models.Single(x => x.Id == selected.Model);
        var reasons = new List<string>
        {
            $"RAM: {hardware.AvailableRamGb:0.0} GB available of {hardware.TotalRamGb:0.0} GB; {hardware.CpuThreads} logical CPU cores.",
            $"Selected model kept: {selected.Model}. These are conservative estimates, not a benchmark."
        };
        // CUDA 12.8 wheels require a recent NVIDIA driver; older/unknown devices stay on CPU.
        var gpu = hardware.Gpus.Where(x => x.ComputeCapability >= 7.5
                && Version.TryParse(x.Driver, out var driver) && driver.Major >= 570
                && x.FreeGb >= Math.Max(selected.Diarization ? 6 : 3, model.SuggestedGpuGb))
            .OrderByDescending(x => x.FreeGb).FirstOrDefault();
        result.CpuThreads = Math.Clamp(hardware.CpuThreads / 2, 1, 16);
        result.BatchSize = 1;
        if (gpu is not null)
        {
            result.Device = "cuda"; result.GpuIndex = gpu.Index; result.ComputeType = "float16";
            result.BatchSize = gpu.FreeGb >= model.SuggestedGpuGb + 6 ? 4 : 1;
            reasons.Add($"NVIDIA GPU {gpu.Index}: {gpu.Name}, {gpu.FreeGb:0.0} GB free. Recommend CUDA, float16, batch {result.BatchSize}.");
            reasons.Add("Setup verifies PyTorch and CTranslate2 CUDA support. A compatible driver/runtime is still required.");
        }
        else
        {
            result.Device = "cpu";
            result.ComputeType = hardware.AvailableRamGb >= model.SuggestedCpuGb + 6 ? "float32" : "int8";
            reasons.Add($"Recommend CPU, {result.ComputeType}, batch 1. No NVIDIA GPU with verified capability, driver and sufficient free memory was found.");
            if (hardware.Gpus.Count > 0) reasons.Add("A smaller model may fit your GPU. The selected model has not been changed.");
            reasons.Add("CPU processing, particularly large-v3, can take longer than the recording itself.");
        }
        if (hardware.AvailableRamGb < Math.Max(4, model.SuggestedCpuGb / 2))
            reasons.Add("Available RAM is low. Close other applications or consider a smaller model before processing.");
        return new(result, hardware, reasons);
    }

    public static async Task<LocalWhisperSettings> ResolveAsync(LocalWhisperSettings selected, CancellationToken token)
    {
        selected.Validate();
        var result = selected.Copy();
        if (result.Device == "auto")
        {
            var recommendation = Recommend(selected, await DetectAsync(token)).Settings;
            result.Device = recommendation.Device; result.GpuIndex = recommendation.GpuIndex;
        }
        if (result.ComputeType == "auto") result.ComputeType = result.Device == "cuda" ? "float16" : "int8";
        result.Validate();
        return result;
    }
}
