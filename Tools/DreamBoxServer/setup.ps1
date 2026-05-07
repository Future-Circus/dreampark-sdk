# DreamBox Dev Server — First-Time Setup (Windows)
#
# Validates or installs prerequisites, builds, and smoke-tests the relay.
#
# Usage:
#   .\setup.ps1          # full setup (install + build + smoke test)
#   .\setup.ps1 -Check   # validate environment only, no install prompts

param(
    [switch]$Check
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Project = Join-Path $ScriptDir "DreamBoxRelay.csproj"
$Errors = 0

function Write-Pass  { param($msg) Write-Host "  PASS  " -ForegroundColor Green -NoNewline; Write-Host $msg }
function Write-Fail  { param($msg) Write-Host "  FAIL  " -ForegroundColor Red -NoNewline; Write-Host $msg; $script:Errors++ }
function Write-Info  { param($msg) Write-Host "  INFO  " -ForegroundColor Cyan -NoNewline; Write-Host $msg }
function Write-Warn  { param($msg) Write-Host "  WARN  " -ForegroundColor Yellow -NoNewline; Write-Host $msg }

Write-Host ""
Write-Host "DreamBox Dev Server — Setup" -ForegroundColor White
Write-Host ([string]::new([char]0x2500, 37))
Write-Host ""

# ── Step 1: Check .NET 9 SDK ───────────────────────────────────────
Write-Host "[1/4] .NET 9 SDK" -ForegroundColor White

$dotnetAvailable = $false
try {
    $dotnetVer = & dotnet --version 2>$null
    $dotnetAvailable = $true
    if ($dotnetVer -like "9.*") {
        Write-Pass ".NET SDK $dotnetVer"
    } else {
        Write-Warn ".NET SDK found ($dotnetVer) but .NET 9 expected"
        Write-Warn "The server targets .NET 9 — other versions may not work"
    }
} catch {
    Write-Fail ".NET SDK not found"

    if (-not $Check) {
        $hasWinget = $false
        try { winget --version 2>$null | Out-Null; $hasWinget = $true } catch {}

        if ($hasWinget) {
            $answer = Read-Host "  Install .NET 9 SDK via winget? [y/N]"
            if ($answer -match '^[Yy]$') {
                Write-Info "Running: winget install Microsoft.DotNet.SDK.9"
                winget install Microsoft.DotNet.SDK.9 --accept-source-agreements --accept-package-agreements
                # Refresh PATH for this session
                $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH", "User")
                try {
                    $dotnetVer = & dotnet --version 2>$null
                    $dotnetAvailable = $true
                    Write-Pass ".NET SDK installed: $dotnetVer"
                    $script:Errors--
                } catch {
                    Write-Fail "Installation completed but 'dotnet' still not on PATH"
                    Write-Info "Close and reopen your terminal, then run this script again"
                }
            }
        } else {
            Write-Fail "winget not found — install .NET 9 SDK manually:"
            Write-Info "https://dotnet.microsoft.com/download/dotnet/9.0"
        }
    }
}

Write-Host ""

# ── Step 2: Restore packages ──────────────────────────────────────
Write-Host "[2/4] Restore packages" -ForegroundColor White

if (-not $dotnetAvailable) {
    Write-Fail "Skipped — .NET SDK not available"
} else {
    try {
        & dotnet restore $Project --nologo --verbosity quiet 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Pass "dotnet restore succeeded" }
        else { Write-Fail "dotnet restore failed (exit code $LASTEXITCODE)" }
    } catch {
        Write-Fail "dotnet restore failed: $_"
    }
}

Write-Host ""

# ── Step 3: Build ─────────────────────────────────────────────────
Write-Host "[3/4] Build (Release)" -ForegroundColor White

if (-not $dotnetAvailable) {
    Write-Fail "Skipped — .NET SDK not available"
} else {
    try {
        & dotnet build $Project --configuration Release --nologo --verbosity quiet 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Pass "dotnet build succeeded" }
        else { Write-Fail "dotnet build failed (exit code $LASTEXITCODE)" }
    } catch {
        Write-Fail "dotnet build failed: $_"
    }
}

Write-Host ""

# ── Step 4: Smoke test ────────────────────────────────────────────
Write-Host "[4/4] Smoke test" -ForegroundColor White

if ($Check) {
    Write-Info "Skipped (-Check mode)"
} elseif (-not $dotnetAvailable) {
    Write-Fail "Skipped — .NET SDK not available"
} else {
    Write-Info "Starting server with --dev flag..."
    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project `"$ScriptDir`" --configuration Release -- --dev" `
        -PassThru -NoNewWindow -RedirectStandardOutput "NUL"

    $portUp = $false
    $maxWait = 10

    for ($i = 1; $i -le $maxWait; $i++) {
        Start-Sleep -Seconds 1

        if ($proc.HasExited) {
            Write-Fail "Server exited prematurely (within ${i}s)"
            break
        }

        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $tcp.Connect("127.0.0.1", 7780)
            $tcp.Close()
            $portUp = $true
            break
        } catch {
            # Port not ready yet
        }
    }

    if ($portUp) {
        Write-Pass "Server started and port 7780 responded (${i}s)"
    } elseif (-not $proc.HasExited) {
        Write-Fail "Server running but port 7780 did not respond within ${maxWait}s"
    }

    # Kill the server
    if (-not $proc.HasExited) {
        try {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Write-Info "Server stopped"
        } catch {}
    }
}

# ── Summary ───────────────────────────────────────────────────────
Write-Host ""
Write-Host ([string]::new([char]0x2500, 37))

if ($Errors -eq 0) {
    Write-Host "  Setup complete — all checks passed!" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Start the server:"
    Write-Host "    cd Tools\DreamBoxServer"
    Write-Host "    dotnet run --configuration Release -- --dev"
    Write-Host ""
    Write-Host "  Or use the Unity menu:"
    Write-Host "    DreamPark > Multiplayer > Start Local Server"
    exit 0
} else {
    Write-Host "  Setup failed — $Errors error(s) above." -ForegroundColor Red
    exit 1
}
