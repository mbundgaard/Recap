using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recap.Updates;

public sealed record UpdateManifest(int Schema, string Version, string Runtime, string FileName, string Sha256, long Size);
public sealed record AvailableUpdate(UpdateManifest Manifest, byte[] ManifestBytes, byte[] Signature);

public static class UpdateTrust
{
    public const string Repository = "mbundgaard/Recap";
    public const string ExecutableName = "Recap-win-x64.exe";
    public const long MaximumPackageSize = 512L * 1024 * 1024;
    public static Version CurrentVersion => ParseVersion(
        typeof(UpdateTrust).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0].Split('-')[0]);
    public static bool SupportsInstallation => RuntimeInformation.ProcessArchitecture == Architecture.X64
        && typeof(UpdateTrust).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(x => x.Key == "RecapSingleFile" && x.Value == "true");

    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+$") || !Version.TryParse(value, out var version))
            throw new InvalidDataException("An update must have a stable major.minor.patch version.");
        return version;
    }

    public static string PublicKey
    {
        get
        {
            using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("Recap.UpdatePublicKey")
                ?? throw new InvalidOperationException("The trusted update key is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static UpdateManifest Verify(byte[] manifestBytes, byte[] signature, string publicKey)
    {
        if (manifestBytes.Length is 0 or > 8192 || signature.Length is 0 or > 1024)
            throw new InvalidDataException("Invalid update metadata size.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("The update signature is invalid. Nothing will be installed.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes)
            ?? throw new InvalidDataException("Missing update manifest.");
        if (manifest.Schema != 1 || manifest.Runtime != "win-x64" || manifest.FileName != ExecutableName
            || manifest.Size <= 0 || manifest.Size > MaximumPackageSize
            || manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("The signed update manifest is not supported.");
        ParseVersion(manifest.Version);
        return manifest;
    }

    public static async Task VerifyFileAsync(string path, UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != manifest.Size) throw new InvalidDataException("The update download is incomplete.");
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!Convert.ToHexString(hash).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update checksum is invalid. Nothing will be installed.");
    }
}
