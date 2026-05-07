#!/usr/bin/env bash
# DreamBox Dev Server — First-Time Setup
#
# Validates or installs prerequisites, builds, and smoke-tests the relay.
#
# Usage:
#   ./setup.sh          # full setup (install + build + smoke test)
#   ./setup.sh --check  # validate environment only, no install prompts

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/DreamBoxRelay.csproj"
CHECK_ONLY=false

if [[ "${1:-}" == "--check" ]]; then
    CHECK_ONLY=true
fi

# ── Colors ──────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

pass()  { echo -e "${GREEN}${BOLD}  PASS${NC}  $1"; }
fail()  { echo -e "${RED}${BOLD}  FAIL${NC}  $1"; }
info()  { echo -e "${CYAN}${BOLD}  INFO${NC}  $1"; }
warn()  { echo -e "${YELLOW}${BOLD}  WARN${NC}  $1"; }

ERRORS=0

echo ""
echo -e "${BOLD}DreamBox Dev Server — Setup${NC}"
echo "─────────────────────────────────────"
echo ""

# ── Step 1: Check .NET 9 SDK ───────────────────────────────────────
echo -e "${BOLD}[1/4] .NET 9 SDK${NC}"

if command -v dotnet >/dev/null 2>&1; then
    DOTNET_VER=$(dotnet --version 2>/dev/null || echo "unknown")
    if [[ "$DOTNET_VER" == 9.* ]]; then
        pass ".NET SDK $DOTNET_VER"
    else
        warn ".NET SDK found ($DOTNET_VER) but .NET 9 expected"
        warn "The server targets .NET 9 — other versions may not work"
    fi
else
    fail ".NET SDK not found"
    ERRORS=$((ERRORS + 1))

    if [[ "$CHECK_ONLY" == false ]]; then
        echo ""
        if [[ "$(uname)" == "Darwin" ]]; then
            # macOS
            if command -v brew >/dev/null 2>&1; then
                echo -n "  Install .NET 9 SDK via Homebrew? [y/N] "
                read -r answer
                if [[ "$answer" =~ ^[Yy]$ ]]; then
                    info "Running: brew install dotnet-sdk"
                    brew install dotnet-sdk
                    if command -v dotnet >/dev/null 2>&1; then
                        pass ".NET SDK installed: $(dotnet --version)"
                        ERRORS=$((ERRORS - 1))
                    else
                        fail "Installation completed but 'dotnet' still not on PATH"
                        info "Try: export PATH=\"/usr/local/share/dotnet:\$PATH\""
                    fi
                fi
            else
                fail "Homebrew not found — install .NET 9 SDK manually:"
                info "https://dotnet.microsoft.com/download/dotnet/9.0"
                info "Or install Homebrew first: https://brew.sh"
            fi
        else
            # Linux
            fail "Install .NET 9 SDK manually for your distro:"
            echo ""
            info "Ubuntu/Debian:"
            info "  sudo apt-get update && sudo apt-get install -y dotnet-sdk-9.0"
            echo ""
            info "Fedora/RHEL:"
            info "  sudo dnf install dotnet-sdk-9.0"
            echo ""
            info "Or see: https://learn.microsoft.com/en-us/dotnet/core/install/linux"
        fi
    fi
fi

echo ""

# ── Step 2: Restore packages ──────────────────────────────────────
echo -e "${BOLD}[2/4] Restore packages${NC}"

if ! command -v dotnet >/dev/null 2>&1; then
    fail "Skipped — .NET SDK not available"
    ERRORS=$((ERRORS + 1))
else
    if dotnet restore "$PROJECT" --nologo --verbosity quiet 2>&1; then
        pass "dotnet restore succeeded"
    else
        fail "dotnet restore failed"
        ERRORS=$((ERRORS + 1))
    fi
fi

echo ""

# ── Step 3: Build ─────────────────────────────────────────────────
echo -e "${BOLD}[3/4] Build (Release)${NC}"

if ! command -v dotnet >/dev/null 2>&1; then
    fail "Skipped — .NET SDK not available"
    ERRORS=$((ERRORS + 1))
else
    if dotnet build "$PROJECT" --configuration Release --nologo --verbosity quiet 2>&1; then
        pass "dotnet build succeeded"
    else
        fail "dotnet build failed"
        ERRORS=$((ERRORS + 1))
    fi
fi

echo ""

# ── Step 4: Smoke test ────────────────────────────────────────────
echo -e "${BOLD}[4/4] Smoke test${NC}"

if [[ "$CHECK_ONLY" == true ]]; then
    info "Skipped (--check mode)"
elif ! command -v dotnet >/dev/null 2>&1; then
    fail "Skipped — .NET SDK not available"
    ERRORS=$((ERRORS + 1))
else
    info "Starting server with --dev flag..."
    # Start server in background
    dotnet run --project "$SCRIPT_DIR" --configuration Release -- --dev &
    SERVER_PID=$!

    # Wait up to 10 seconds for port 7780 (web panel) to respond
    WAITED=0
    MAX_WAIT=10
    PORT_UP=false

    while [[ $WAITED -lt $MAX_WAIT ]]; do
        sleep 1
        WAITED=$((WAITED + 1))

        # Check if process is still running
        if ! kill -0 "$SERVER_PID" 2>/dev/null; then
            fail "Server exited prematurely (within ${WAITED}s)"
            ERRORS=$((ERRORS + 1))
            break
        fi

        # Try connecting to the web panel port
        if (echo > /dev/tcp/127.0.0.1/7780) 2>/dev/null; then
            PORT_UP=true
            break
        fi
    done

    if [[ "$PORT_UP" == true ]]; then
        pass "Server started and port 7780 responded (${WAITED}s)"
    elif kill -0 "$SERVER_PID" 2>/dev/null; then
        fail "Server running but port 7780 did not respond within ${MAX_WAIT}s"
        ERRORS=$((ERRORS + 1))
    fi

    # Kill the server
    if kill -0 "$SERVER_PID" 2>/dev/null; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
        info "Server stopped"
    fi
fi

# ── Summary ───────────────────────────────────────────────────────
echo ""
echo "─────────────────────────────────────"
if [[ $ERRORS -eq 0 ]]; then
    echo -e "${GREEN}${BOLD}  Setup complete — all checks passed!${NC}"
    echo ""
    echo "  Start the server:"
    echo "    cd Tools/DreamBoxServer"
    echo "    dotnet run --configuration Release -- --dev"
    echo ""
    echo "  Or use the Unity menu:"
    echo "    DreamPark → Multiplayer → Start Local Server"
    exit 0
else
    echo -e "${RED}${BOLD}  Setup failed — $ERRORS error(s) above.${NC}"
    exit 1
fi
