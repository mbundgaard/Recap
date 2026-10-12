[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$project = Join-Path $PSScriptRoot 'src/Recap/Recap.csproj'
$output = Join-Path $PSScriptRoot "artifacts/$Runtime"

# Clean only this script's publish directory so stale files cannot be shipped.
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

& dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $output `
    "-p:Version=$Version" `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit code $LASTEXITCODE)."
}

$files = @(Get-ChildItem -LiteralPath $output -Force)
if ($files.Count -ne 1 -or $files[0].Name -ne 'Recap.exe' -or $files[0].PSIsContainer) {
    throw "Expected only Recap.exe in '$output'. Found: $($files.Name -join ', ')"
}

Write-Host "Built single executable: $output/Recap.exe"
