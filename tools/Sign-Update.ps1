#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($env:RECAP_UPDATE_SIGNING_KEY)) {
    throw 'RECAP_UPDATE_SIGNING_KEY is required; refusing to publish an unsigned update manifest.'
}
$package = Get-Item -LiteralPath $PackagePath
if ($package.Name -ne 'Recap-win-x64.exe') { throw 'Unexpected update package name.' }
$manifest = [ordered]@{
    Schema = 1
    Version = $Version
    Runtime = 'win-x64'
    FileName = $package.Name
    Sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Size = $package.Length
} | ConvertTo-Json -Compress
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($manifest)
$rsa = [Security.Cryptography.RSA]::Create()
$verifier = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($env:RECAP_UPDATE_SIGNING_KEY)
    $signature = $rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $publicKey = Get-Content (Join-Path $PSScriptRoot '../src/Recap/Updates/update-public-key.pem') -Raw
    $verifier.ImportFromPem($publicKey)
    if (-not $verifier.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
        throw 'The signing secret does not match the public key embedded in Recap.'
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'update-manifest.json'), $bytes)
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'update-manifest.sig'), [Convert]::ToBase64String($signature), [Text.Encoding]::ASCII)
} finally {
    $rsa.Dispose()
    $verifier.Dispose()
    Remove-Item Env:RECAP_UPDATE_SIGNING_KEY -ErrorAction SilentlyContinue
}
Write-Host 'Created and verified the signed update manifest.'
