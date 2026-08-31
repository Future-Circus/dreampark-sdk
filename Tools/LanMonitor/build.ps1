# Build self-contained single-file LanMonitor binaries for all dev platforms.
#
# Usage:
#   .\build.ps1                 # build all platforms
#   .\build.ps1 osx-arm64       # build one specific platform
#   .\build.ps1 osx-arm64 win-x64
#
# Output: dist\<rid>\LanMonitor(.exe) plus the adjacent config\ and wwwroot\.
#
# Only needed for machines without the .NET 9 SDK — the Unity menu prefers
# `dotnet run` when the SDK is present.

param([string[]]$Rids)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $ScriptDir "LanMonitor.csproj"
$OutRoot = Join-Path $ScriptDir "dist"

if (-not $Rids -or $Rids.Count -eq 0) {
    $Rids = @("win-x64", "osx-arm64", "osx-x64", "linux-x64")
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "'dotnet' not found on PATH. Install the .NET 9 SDK from https://dotnet.microsoft.com/download/dotnet/9.0"
    exit 1
}

Write-Host "dotnet: $(dotnet --version)"
Write-Host "building: $($Rids -join ' ')"
Write-Host "output:   $OutRoot"
Write-Host ""

foreach ($rid in $Rids) {
    $out = Join-Path $OutRoot $rid
    Write-Host "-> publishing $rid..."
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet publish $Project `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $out `
        --nologo `
        --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Write-Error "publish failed for $rid"; exit 1 }
    Write-Host "   done: $out"
}

Write-Host ""
Write-Host "all builds complete."
