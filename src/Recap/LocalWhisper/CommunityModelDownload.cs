using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Recap.LocalWhisper;

/// <summary>Downloads only the bundled, hash-pinned community-1 asset inventory.</summary>
public static class CommunityModelDownload
{
    // Pinned to the model-only repository commit, independent of subsequent application changes.
    public const string RepositoryRevision = "6f1830de7287e11648a2e31fde5c0d8e54d6ad60";
    private sealed record Asset(string Path, long Size, string Sha256);

    public static async Task EnsureAsync(CancellationToken cancellation)
    {
        using var manifest = typeof(CommunityModelDownload).Assembly.GetManifestResourceStream("Recap.LocalWhisper.community-1.json")
            ?? throw new InvalidOperationException("The speaker model manifest is missing.");
        var assets = await JsonSerializer.DeserializeAsync<Asset[]>(manifest, cancellationToken: cancellation)
            ?? throw new InvalidDataException("Invalid speaker model manifest.");
        var root = Path.Combine(LocalWhisperSettings.Root, "models", "diarization", "community-1");
        Directory.CreateDirectory(root);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        foreach (var asset in assets)
        {
            cancellation.ThrowIfCancellationRequested();
            if (asset.Path.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\\') || part.Contains(':'))
                || asset.Size < 0 || asset.Size > 40 * 1024 * 1024)
                throw new InvalidDataException("Invalid speaker model asset.");
            var target = Path.Combine(root, asset.Path.Replace('/', Path.DirectorySeparatorChar));
            if (await MatchesAsync(target, asset, cancellation)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".download";
            try
            {
                var url = $"https://raw.githubusercontent.com/mbundgaard/Recap/{RepositoryRevision}/models/community-1/{asset.Path}";
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long total = 0; int count;
                    while ((count = await source.ReadAsync(buffer, cancellation)) != 0)
                    {
                        total += count;
                        if (total > asset.Size) throw new InvalidDataException("Speaker model download exceeds its expected size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    }
                }
                if (!await MatchesAsync(temporary, asset, cancellation)) throw new InvalidDataException("Speaker model checksum mismatch.");
                File.Move(temporary, target, true);
            }
            finally { File.Delete(temporary); }
        }
    }

    private static async Task<bool> MatchesAsync(string path, Asset asset, CancellationToken cancellation)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
