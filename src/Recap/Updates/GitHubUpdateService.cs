using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Recap.Updates;

public sealed class GitHubUpdateService
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Recap-Updater/1.0");
        return client;
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await GetAsync(new Uri($"https://api.github.com/repos/{UpdateTrust.Repository}/releases/latest"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var releaseBytes = await ReadLimitedAsync(response, 256 * 1024, cancellationToken);
        using var release = JsonDocument.Parse(releaseBytes);
        var root = release.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) return null;
        var version = UpdateTrust.ParseVersion(tag[1..]);
        if (version <= UpdateTrust.CurrentVersion) return null;
        var files = root.GetProperty("assets").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToHashSet();
        if (!files.Contains("update-manifest.json") || !files.Contains("update-manifest.sig") || !files.Contains(UpdateTrust.ExecutableName))
            throw new InvalidDataException("This release has no signed update package. Install it manually from GitHub if you trust it.");
        var manifestBytes = await DownloadSmallAsync(AssetUri(tag[1..], "update-manifest.json"), 8192, cancellationToken);
        var signatureText = await DownloadSmallAsync(AssetUri(tag[1..], "update-manifest.sig"), 2048, cancellationToken);
        var signature = Convert.FromBase64String(Encoding.ASCII.GetString(signatureText).Trim());
        var manifest = UpdateTrust.Verify(manifestBytes, signature, UpdateTrust.PublicKey);
        if (manifest.Version != tag[1..]) throw new InvalidDataException("Release and signed manifest versions differ.");
        return new(manifest, manifestBytes, signature);
    }

    public async Task<string> DownloadAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var manifest = UpdateTrust.Verify(update.ManifestBytes, update.Signature, UpdateTrust.PublicKey);
        if (UpdateTrust.ParseVersion(manifest.Version) <= UpdateTrust.CurrentVersion)
            throw new InvalidDataException("Refusing to install an older or identical version.");
        UpdateInstaller.CleanupOldStaging();
        var directory = Path.Combine(UpdateInstaller.UpdatesFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, UpdateTrust.ExecutableName);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "update-manifest.json"), update.ManifestBytes, cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(directory, "update-manifest.sig"), update.Signature, cancellationToken);
            using var response = await GetAsync(AssetUri(manifest.Version, manifest.FileName), cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != manifest.Size)
                throw new InvalidDataException("Unexpected update download size.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += count;
                    if (received > manifest.Size) throw new InvalidDataException("Update exceeded its signed size.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress.Report(100d * received / manifest.Size);
                }
            }
            await UpdateTrust.VerifyFileAsync(target, manifest, cancellationToken);
            return directory;
        }
        catch
        {
            try { Directory.Delete(directory, true); } catch { /* Leave diagnostic files if locked. */ }
            throw;
        }
    }

    private static Uri AssetUri(string version, string name) =>
        new($"https://github.com/{UpdateTrust.Repository}/releases/download/v{version}/{name}");

    private static async Task<byte[]> DownloadSmallAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await GetAsync(uri, token);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response, limit, token);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var destination = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (destination.Length + count > limit) throw new InvalidDataException("Update metadata is too large.");
            destination.Write(buffer, 0, count);
        }
        return destination.ToArray();
    }

    private static async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != "https" || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("The update server redirected to an untrusted location.");
            var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("Invalid update redirect.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("Too many update redirects.");
    }
}
