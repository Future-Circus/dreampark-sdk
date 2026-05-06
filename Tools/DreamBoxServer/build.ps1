<#
.SYNOPSIS
  Build self-contained single-file DreamBoxRelay binaries for all dev platforms.

.EXAMPLE
  .\build.ps1
  .\build.ps1 win-x64
  .\build.ps1 win-x64 osx-arm64

.DESCRIPTION
  Output lands in dist\<rid>\DreamBoxRelay(.exe) with adjacent config\ and wwwroot\ folders.
#>
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Rids
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project   = Join-Path $ScriptDir "DreamBoxRelay.csproj"
$OutRoot   = Join-Path $ScriptDir "dist"

$AllRids = @("win-x64", "osx-arm64", "osx-x64", "linux-x64")

if (-not $Rids -or $Rids.Count -eq 0) {
  $Rids = $AllRids
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Error "dotnet not found on PATH. Install the .NET 9 SDK from https://dotnet.microsoft.com/download/dotnet/9.0"
}

Write-Host "dotnet: $(dotnet --version)"
Write-Host "building: $($Rids -join ', ')"
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

  if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed for $rid"
  }
  Write-Host "   done: $out"
}

Write-Host ""
Write-Host "all builds complete."
