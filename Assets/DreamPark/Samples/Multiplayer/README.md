# DreamPark Samples

Ready-to-run examples that demonstrate SDK features. Open with `DreamPark → ... → Create Test Scene` and hit Play.

---

## Multiplayer Test

End-to-end sanity check for the networking stack. Talks to the bundled local dev server (no hardware required).

### Quick start

1. **Start the local relay:** `DreamPark → Multiplayer → Start Local Server`.
   A console window appears showing the relay booting on :7777 and the web control panel on http://127.0.0.1:7780.

2. **Generate the test scene (once):** `DreamPark → Multiplayer → Create Test Scene`.
   This writes `Assets/DreamPark/Samples/MultiplayerTest.unity` and opens it.

3. **Press Play ▶.**
   An on-screen panel shows the connection state and three buttons.

4. **Click "Connect to Local Server."**
   The state label switches from `Disconnected` → `Connecting` → `Connected` within a second or two. If you watch the server's web panel, the peer count increments.

5. **Click "Send Random Color."**
   The cube in the scene changes colour.

### What the scene contains

- `Floor` — a grey plane so the cube has somewhere to sit.
- `TestProp` — a cube with a `NetId` and a `TestNetObject` component. `TestNetObject` subscribes to `NetId.OnNetEvent` and applies any `{r, g, b}` payload it receives to its material colour.
- `DreamBoxClient` — the singleton LiteNetLib client. Pre-configured to talk to `127.0.0.1:7777` with `connectionKey: "dreambox"`, `connectOnStart: false` (so it only connects when you click the button — useful for iterating).
- `TestUI` + `TestUIController` — the three-button overlay UI. Wires the buttons to the client and test prop at Start.

### What you're actually testing

- **Discovery & connection:** the connect button calls `DreamBoxClient.Connect("127.0.0.1", 7777, "dreambox")` directly (skipping UDP discovery). Confirms LiteNetLib can reach the relay and handshake with the correct key.
- **Dispatch plumbing:** "Send Random Color" calls `DreamBoxClient.SendToNetId(netId, "color", "{...}")`. The relay broadcasts to all other peers; on each client, `DreamBoxClient.HandleEvent` parses the inbound JSON and calls `NetRegistry.Dispatch(netId, payload)`, which invokes `NetId.OnNetEvent` on the matching object.
- **Single-client convenience:** the relay excludes the sender from `SendToAll`, so a lone client wouldn't receive its own message back. `TestNetObject.SendRandomColor` applies the colour locally as well as sending, so you get a visible result with just one client. Spin up a second Unity Editor (or a standalone build of this scene) and you'll see live colour sync between them.

### Regenerating the scene

The scene file is built programmatically — no hand-authored YAML. Run `DreamPark → Multiplayer → Create Test Scene` again to regenerate (you'll get a dialog with Open / Cancel / Regenerate). Useful if the scene gets broken by a Unity upgrade, or if you've diverged the sample and want a clean copy.

### Files

```
Assets/DreamPark/Samples/
├── README.md                 ← this file
├── TestNetObject.cs          ← attaches to a NetId'd object; reacts to colour events
├── MultiplayerTestUI.cs      ← UI controller (buttons → client)
└── MultiplayerTest.unity     ← generated on-demand by the Editor menu

Assets/DreamPark/Editor/
└── MultiplayerTestSceneGenerator.cs  ← builds the scene from code
```

### Troubleshooting

**State stays on `Connecting` and never reaches `Connected`.**
The relay isn't actually running. Check the console window that popped up when you chose "Start Local Server". If the window closed or shows an error, open `DreamPark → Multiplayer → Open Control Panel` — if the browser can't reach it, the server crashed. Check the Unity console for the launch error.

**State cycles Connected → Reconnecting → Connected repeatedly.**
Something is churning the relay — probably a port conflict. Stop the server, check `lsof -nP -i :7777` for a stale process, then restart.

**Click "Send Random Color" but cube doesn't change.**
Check the Unity console for a warning from `TestNetObject` about payload parsing. If there's no warning at all, the button isn't wired (rare — the scene generator connects it at build time). Regenerate the scene.

**Two Editors on the same machine don't see each other.**
They should — the relay bounces messages to all *other* peers and the loopback beacon reaches both. If you're not seeing colour sync between them, check that both are connected (state = Connected) in their respective UIs and both point at `127.0.0.1:7777`.
