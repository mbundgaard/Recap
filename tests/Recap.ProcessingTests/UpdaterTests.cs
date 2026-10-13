using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Recap.Updates;

static class UpdaterTests
{
    public static async Task RunAsync(string root)
    {
        using var key = RSA.Create(3072);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var bytes = "test executable payload"u8.ToArray();
        var manifest = new UpdateManifest(1, "9.1.0", "win-x64", UpdateTrust.ExecutableName,
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        byte[] Encode(UpdateManifest value) => JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var encoded = Encode(manifest);
        var signature = Sign(encoded);
        Require(UpdateTrust.Verify(encoded, signature, publicKey) == manifest, "Valid update signature must verify");
        var altered = Encode(manifest with { Version = "9.2.0" });
        Reject(() => UpdateTrust.Verify(altered, signature, publicKey), "Tampered manifest must be rejected");
        using var stranger = RSA.Create(3072);
        Reject(() => UpdateTrust.Verify(encoded, Sign(encoded), stranger.ExportSubjectPublicKeyInfoPem()), "Untrusted signing key must be rejected");
        foreach (var invalid in new[]
        {
            manifest with { FileName = "../settings.json" },
            manifest with { Runtime = "win-arm64" },
            manifest with { Size = UpdateTrust.MaximumPackageSize + 1 },
            manifest with { Sha256 = "invalid" },
            manifest with { Version = "9.1.0-beta" },
            manifest with { Schema = 2 }
        })
        {
            var value = Encode(invalid);
            Reject(() => UpdateTrust.Verify(value, Sign(value), publicKey), "Unsupported signed metadata must be rejected");
        }
        var folder = Path.Combine(root, "updater-checks");
        Directory.CreateDirectory(folder);
        var package = Path.Combine(folder, "package.exe");
        await File.WriteAllBytesAsync(package, bytes);
        await UpdateTrust.VerifyFileAsync(package, manifest);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(package, bytes);
        await RejectAsync(() => UpdateTrust.VerifyFileAsync(package, manifest), "Tampered payload must be rejected");
        await File.WriteAllBytesAsync(package, bytes[..3]);
        await RejectAsync(() => UpdateTrust.VerifyFileAsync(package, manifest), "Partial download must be rejected");

        var installed = Path.Combine(folder, "Recap.exe");
        var backup = installed + ".previous";
        var settings = Path.Combine(folder, "settings.json");
        var audio = Path.Combine(folder, "recording.wav");
        await File.WriteAllTextAsync(installed, "old version");
        await File.WriteAllTextAsync(settings, "settings must survive");
        await File.WriteAllTextAsync(audio, "audio must survive");
        await File.WriteAllTextAsync(package, "new version");
        await RejectAsync(() => UpdateInstaller.ReplaceWithRollbackAsync(package, installed, backup,
            () => throw new IOException("Simulated startup failure")), "Failed startup must report failure");
        Require(await File.ReadAllTextAsync(installed) == "old version", "Failed startup must restore old executable");
        await File.WriteAllTextAsync(package, "new version");
        await UpdateInstaller.ReplaceWithRollbackAsync(package, installed, backup, () => Task.CompletedTask);
        Require(await File.ReadAllTextAsync(installed) == "new version" && await File.ReadAllTextAsync(backup) == "old version",
            "Successful update must retain a rollback executable");
        Require(await File.ReadAllTextAsync(settings) == "settings must survive" && await File.ReadAllTextAsync(audio) == "audio must survive",
            "Updating must preserve settings and recordings");
        // Confirm the shipped trust anchor loads without exposing or requiring the private key.
        using var embedded = RSA.Create();
        embedded.ImportFromPem(UpdateTrust.PublicKey);
        Require(embedded.KeySize >= 3072, "Production update trust anchor must use RSA-3072 or stronger");
        Console.WriteLine("PASS: signed manifests, tamper rejection, package integrity, atomic replacement, startup rollback and user-data preservation.");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidDataException or CryptographicException) { return; }
        throw new Exception(message);
    }
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
        throw new Exception(message);
    }
}
