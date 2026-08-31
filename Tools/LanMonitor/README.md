# DreamPark LAN Monitor

A read-only observer for LAN multiplayer sessions. Paste in a `parkId`, and it
reports every message the headsets in that park send each other.

It does **not** host a relay and does **not** broadcast a beacon. Starting it
cannot preempt a peer election or otherwise disturb the session you are trying
to debug. That is why it is a separate binary from `Tools/DreamBoxServer` and
not a mode of it — see [Why a separate tool](#why-a-separate-tool).

---

## Start it

From the Unity Editor:

```
DreamPark → Multiplayer → Start LAN Monitor
```

That launches the process, waits for its panel to come up, and opens
<http://127.0.0.1:7781> for you. Stop it with **Stop LAN Monitor**.

If a `NetSessionArbiter` in the open scene already has a `parkId`, the monitor
is launched following that park and attaches on its own. Otherwise, type a
parkId into the **Pin** field, or click a session in the sidebar.

**Prerequisite:** the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0),
or a prebuilt binary in `dist/` (`./build.sh`). Same requirement as the dev
relay, and if you can already run *Start Local Server* you can run this.

From a terminal:

```bash
cd Tools/LanMonitor
dotnet run -c Release -- --dev --park my-park-id
```

---

## How it works, and what that costs

The relay is a dumb pipe: it rebroadcasts every inbound message verbatim to all
*other* connected peers, and the peer host connects to its own relay over
`127.0.0.1` as an ordinary client. So a peer that only listens sees **100% of
session traffic, the host's own events included**.

Joining is how you get that view, and joining is not free:

- **It takes a peer slot.** `MaxPeers` is 16, and the host's
  `MaxPeersPerAddress` is 2. A laptop already running an Editor instance in the
  session has one slot left.
- **Sender attribution is gone.** The relay adds no origin annotation, and
  everything reaches the monitor over its single connection to the host. The
  only "who sent this" available is the payload's own `u` field, by convention.
  If you need per-peer attribution more than you need non-interference, the dev
  relay in `Tools/DreamBoxServer` *is* the host and its panel has it.
- **You cannot see dropped messages.** `PeerRelayServer` silently drops past
  60 msg/s per peer, and it drops them *inbound*, before fan-out. The monitor
  sees the survivors. The host logs the drops as `[PeerRelay] Rate limit`.
- **netIds arrive as hashes.** Nothing on the wire maps them to object names.
  To match them up, turn on `Verbose Net Logs` on a headset and compare its
  `Registered NetId` lines.

---

## The panel

| | |
|---|---|
| **Sessions** | Every dream-pub beacon heard on the LAN — hostType, hostId, parkId, channel, protocol version, advertised `msgCap`. Click a live one to attach. |
| **Pin** | A parkId to follow. The monitor attaches when a session advertising it appears, and **re-attaches across host re-elections** — see below. |
| **Feed** | One row per observed message: time, `type`, `netId`, `u`, a payload preview, and size. Click a row for the full JSON. |
| **Filter** | Substring match across type, netId, u and the raw text. |
| **Pause** | Freezes the view. The ring buffer keeps filling, so resuming replays the gap rather than skipping it. |

### Following a re-election

This is the reason to pin a park rather than a host. When the host headset is
doffed, its relay goes silent with no goodbye and the room re-elects in 1–3 s.
Nothing about the new session is the same — new host, new IP, new port, new
per-session key. A monitor pinned to a host would go dark at exactly the moment
that got interesting. Pinned to a `parkId`, it notices the timeout, re-attaches
to whoever won, and the feed continues. The JSONL capture runs straight through
it, with each line stamped with the session it arrived on.

A DreamBox kiosk beacon carries **no parkId**, so a kiosk is never auto-attached
— that would be a guess, and quietly watching the wrong room is worse than
watching nothing. When a kiosk session is live and your pinned park has not
appeared, the status line says so; attach it by hand.

---

## Captures

Every observed message is appended to `captures/capture-<timestamp>.jsonl`
(`--capture-dir` to move it, `--no-capture` to turn it off). One JSON object per
line, with the original message spliced in verbatim under `msg` when it parsed:

```bash
# every event type seen, by frequency
jq -r '.type // "(unparsed)"' capture-*.jsonl | sort | uniq -c | sort -rn

# everything one object said
jq 'select(.netId == 100523)' capture-*.jsonl

# per-second message rate, against the 60 msg/s peer-host budget
jq -r '.t / 1000 | floor' capture-*.jsonl | uniq -c
```

A message that wasn't valid JSON lands under `rawText` instead of `msg`, and
shows in the feed as **not JSON** rather than being quietly dropped.

---

## Options

```
--dev                     load config/dev.json (or the shipped example)
--config <path>           load a specific config file
--park <parkId>           auto-attach to this park and follow re-elections
--attach host:port[:key]  skip discovery, join this host directly
--channel <prod|sdk>      only auto-attach to sessions on this channel
--panel-port <n>          web panel port (default 7781)
--no-panel                headless: capture to JSONL only
--no-capture              live panel only, nothing written to disk
--capture-dir <path>      where captures land (default ./captures)
--debug                   echo every observed message to stdout
--check                   validate ports and config, then exit
```

---

## Troubleshooting

**No sessions ever appear.**
Beacons are 1 Hz, so a live session shows up within a couple of seconds. If
nothing does:

- Confirm this machine is on the same subnet as the headsets.
- Many corporate and guest networks isolate clients from each other, and phone
  hotspots drop UDP broadcast unpredictably. Use **Attach by hand** with the
  host, port and key from the hosting headset's log.
- SDK builds beacon on channel `sdk` and core builds on `prod`, but the monitor
  does not filter by channel unless you pass `--channel` — every session shows
  in the sidebar regardless, with its channel labelled.

**"could not bind UDP :7700".**
Another process on this machine holds it — usually a Unity Editor running
`DreamBoxClient`, or the dev relay. The monitor asks the OS to share the port
and normally gets it; when it doesn't, discovery is off and the panel says so.
**Attach by hand** still works, and so does `--attach`.

**"host rejected the connection".**
Wrong session key (the beacon went stale mid-re-election — it will retry with
the newest one), or the host is at its 16-peer cap, or this machine already
holds the host's 2-connections-per-address limit.

**The panel doesn't open.**
Another monitor instance is probably holding :7781. `--check` will say so.

---

## Why a separate tool

`Tools/DreamBoxServer` is the reference relay and is kept byte-identical to
`dream-pub` on the Pi, minus its web panel. Adding a client/observe mode there
would drift that source and, worse, would put hosting code in the same process
as the observer.

That matters more than tidiness: **a kiosk beacon always outranks a peer
session** in `NetSessionArbiter`'s ladder. A monitor that ever advertised itself
would preempt the peer election and destroy the session it was brought in to
watch. Keeping the send path out of this binary entirely makes that failure
impossible rather than merely unlikely — `BeaconWatcher` has no `Send`, and
`SessionObserver` has no method that writes to the wire.

---

## File layout

```
Tools/LanMonitor/
├── README.md            ← this file
├── LanMonitor.csproj
├── Program.cs           ← entry point, main loop, CLI
├── MonitorConfig.cs     ← config load + CLI overrides
├── BeaconWatcher.cs     ← UDP :7700 receive-only beacon parser
├── SessionObserver.cs   ← read-only LiteNetLib peer
├── AttachDirector.cs    ← which session to watch, and re-election follow
├── EventLog.cs          ← ring buffer, wire parsing, JSONL capture
├── MonitorState.cs      ← shared state + command queue
├── MonitorPanel.cs      ← HttpListener + JSON API
├── build.sh / build.ps1 ← self-contained publish into dist/
├── config/
│   └── dev.example.json
└── wwwroot/
    └── index.html       ← the panel
```

Related: `Tools/DreamBoxServer` (the relay you host with),
dreampark-core `Docs/LAN-PeerHost-Spec.md` (the protocol),
dreampark-sdk `CLAUDE.md` § Multiplayer (authoring and NetId rules).
