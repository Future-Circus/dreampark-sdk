# DreamBox Dev Server

A local copy of the DreamBox LAN relay (dream-pub) that runs on your Windows / macOS / Linux dev machine. Use it to test Unity builds against without needing physical DreamBox hardware.

Your Unity `DreamBoxClient` auto-discovers this relay over UDP broadcast on port 7700, exactly like the real kiosk. Nothing in the Unity project needs to change.

---

## One-click (recommended)

From the Unity Editor:

```
DreamPark → Dev Server → Start Local Server
```

That single command:

1. Starts the relay (incremental build via `dotnet run`, or a prebuilt binary if present).
2. Waits for the browser control panel to come up.
3. Opens http://localhost:7780 for you.

Stop it later with `DreamPark → Dev Server → Stop Server`.

**Prerequisite:** either the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) installed (fastest dev loop), *or* run `DreamPark → Dev Server → Rebuild Binaries` once to generate a self-contained binary that doesn't need the SDK.

---

## From a terminal

```bash
cd Tools/DreamBoxServer
dotnet run --configuration Release -- --dev
```

Then open http://localhost:7780. Requires the .NET 9 SDK.

---

## What the server does

- Listens on **UDP :7777** (relay traffic) and **UDP :7700** (discovery beacon).
- Accepts LiteNetLib connections using `connectionKey: "dreambox"`.
- Relays every message to all other connected peers (reliable ordered).
- Broadcasts its presence every 2s so `DreamBoxClient` finds it automatically.
- Serves a read-only control panel on **HTTP :7780** (dev config only).

This is the same source as `dream-pub/DreamBoxRelay` on the Pi, with one additional module (`WebControlPanel.cs`) that's a no-op unless `webPanel.enabled` is `true` in config.

---

## Config

Two example configs ship in `config/`:

- `dev.example.json` — localhost dev, web panel on, debug on.
- `dream-pub.example.json` — mirrors the Pi production config, web panel off.

Copy either to `dev.json` / `dream-pub.json` and edit. The loader resolves in this order:

1. `--config <path>` CLI arg
2. `--dev` flag → `config/dev.json` (or `config/dev.example.json` fallback)
3. `DREAM_PUB_CONFIG` env var
4. `./dream-pub.json` next to the binary
5. `./config/dev.json` next to the binary
6. `/etc/dream-pub/dream-pub.json` (Pi only)

### Common tweaks

- **Corporate / guest Wi-Fi blocks UDP broadcast.** Set `discoveryEnabled: false` and point the Unity client at your machine's IP manually.
- **Testing over ZeroTier or another VPN.** Same thing — disable the beacon and configure the client directly.
- **Expose the panel on your LAN.** Change `webPanel.bindAddress` to `0.0.0.0`. Only do this on trusted networks; the panel has no auth.

---

## Web control panel

Auto-refreshing browser UI that shows:

- Peer count, max connections, uptime, last activity
- Server config summary (port, connection key, discovery state)
- Traffic counters (messages relayed, bytes relayed)
- Live peer table (peer ID, endpoint, connection duration)

API endpoints (JSON, read-only):

- `GET /api/status` — config + counters + timestamps
- `GET /api/peers` — connected peer list

Good for quickly checking "is my headset actually connected?" without reading stdout logs.

---

## Building binaries

Produces self-contained single-file executables — no .NET install needed on the target machine. Useful for devs who don't want to install the .NET SDK.

**macOS / Linux:**

```bash
cd Tools/DreamBoxServer
./build.sh              # builds all platforms into dist/
./build.sh osx-arm64    # or one specific platform
```

**Windows (PowerShell):**

```powershell
cd Tools\DreamBoxServer
.\build.ps1             # builds all platforms into dist\
.\build.ps1 win-x64     # or one specific platform
```

Platforms supported: `win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`.

Each build lands in `dist/<rid>/` with `DreamBoxRelay(.exe)` plus its `config/` and `wwwroot/` folders.

---

## Troubleshooting

**"Port 7777 is already in use."**
Another relay or game server is bound. Either kill it (`lsof -i :7777` on macOS/Linux) or change `port` in your config.

**"Web panel won't bind on Windows."**
HttpListener sometimes needs a URL ACL. Run as admin once:

```powershell
netsh http add urlacl url=http://127.0.0.1:7780/ user=Everyone
```

**Unity client doesn't auto-discover the relay.**
- Confirm the server's log shows `[beacon] Broadcasting on :7700`.
- Check that your dev machine and the headset are on the same subnet.
- Many corporate / guest Wi-Fi networks block UDP broadcast. Switch networks or disable discovery and configure the client directly.

**Firewall prompt on first run (macOS/Windows).**
Allow it — the relay needs to accept inbound UDP connections from the headset.

**Menu says "control panel didn't come up" but the server log looks fine.**
Likely port 7780 is already in use by another service. Change `webPanel.port` in `config/dev.json` and restart.

---

## File layout

```
Tools/DreamBoxServer/
├── README.md                 ← this file
├── build.sh                  ← cross-platform publish (bash)
├── build.ps1                 ← cross-platform publish (PowerShell)
├── .gitignore                ← ignores bin/ obj/ dist/
├── DreamBoxRelay.csproj      ← the .NET project
├── Program.cs                ← UDP event loop + wiring
├── RelayConfig.cs            ← config loader + schema
├── DiscoveryBeacon.cs        ← UDP broadcast announcer
├── ServerState.cs            ← shared state (peers, counters)
├── WebControlPanel.cs        ← HttpListener + JSON API
├── config/
│   ├── dev.example.json
│   └── dream-pub.example.json
└── wwwroot/
    └── index.html            ← control panel UI
```

---

## Relationship to the Pi server

This is the same codebase as `dreambox/dream-pub/DreamBoxRelay` with two additions:

1. `webPanel` config section (opt-in; defaults to off)
2. `--dev` config resolution path

Everything else — wire protocol, discovery payload, connection key — is identical. A Unity client that works against this dev server will work against the Pi.
