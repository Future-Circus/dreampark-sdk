# Players: phones, gestures and hands

Phone players, touch gestures, hand-held props and the player head.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

## Phone players & gestures (Oct 2026)

Guests can join on a **phone**: an AR window onto the park, touch input, no hands.
Runtime: `Scripts/Features/Phone/` (`DreamParkDevice`, `DreamParkGestures`, Editor mouse source).

- **The rig is the gate (Oct 23 2026).** A device takes part in a title's gameplay only while it WEARS the
  title's rig: a headset its Player, a phone the title's Phone Player — which the phone wears only in camera /
  full-screen AR (Play tab, sheet down; off in the diorama, with the sheet up, on another tab or paused). Without it
  the phone is a **spectator**: it watches live but never leads (`dp.leader()` / `dp.rank()` skip it), its writes to
  the title's `dp.shared_state` tables are dropped and refused by the writer, its `net_send` is a no-op (except asks:
  kinds ending `_req` / `_request` / `_rq` / `_hi` / `_hello`), `dp.set_state` does nothing, and playlist events /
  `dp.event` / `stop.*` signals / `dp.playlist.score` never leave it. Taps are deliberate: what a tap handler does
  goes through (a start coin, the start circle); for a deferred deliberate action use `dp.spectator_action(fn, ...)`.
  The phone's playlist / queue control (Play, switch, skip, What's Next) is never gated. Scripts that run on every
  device (Game Manager / Game Container) check **`dp.participating()`** and skip gameplay work (claims, reports,
  restarts, level advances, saves) when it is false — guard it for older apps:
  `local function playing() return dp.participating == nil or dp.participating() end`.
  Runtime: `Scripts/Features/Net/DreamParkParticipation.cs`.
- **Default: spectator.** A phone gets no rig unless the title fills the optional **Phone Player**
  slot (see § Packages → Player rig in packages). It still gets the Game Manager, so `dp.player()` is
  nil on a phone without a Phone Player — **title-wide systems belong on the Game Manager**, never only
  on Player.
- **The slot.** Content Uploader → Packages → PHONE PLAYER (optional). The template ships
  `Prefabs/Phone Player.prefab` (PlayerRig; Head/Body/Feet trackers, no hands; `Phone Controls`
  running `Scripts/phone-player.lua.txt`; an authored, stopped `Tap Ripple` ParticleSystem; a
  `RemoteRig/Head` phone slab = what headset players see of a phone user) but leaves the slot EMPTY.
  "Use template Phone Player" fills it in one click. Regenerate the prefab with (the builder ships in the SDK template repo)
  `unity command --project-path . run_script --file Tools/Builders/PhonePlayerBuilder.cs --entry PhonePlayerBuilder.Build`.
- **Device API.** `DreamParkDevice` (the app calls `Set` at boot: iOS → Phone, Android → Headset;
  default Editor / Phone on iOS / Headset). Lua: `dp.device()`, `dp.is_phone()`, `dp.is_headset()`,
  `dp.device_of(playerId)` — other players' device rides `PlayerPresence`'s state resend as optional
  `dv` (absent on older clients → nil). Phone players are otherwise treated like any player.
- **What others see.** `RemotePlayer` draws a peer with a RemoteRig registered for THEIR device
  (`RemoteRig.RegisterTemplate(gameId, "phone"|"headset", template)` — the host app's job, since a
  headset never wears the Phone Player), else the local rig's RemoteRig when the devices match (or the
  peer's device is unknown), else only the name tag.
- **Gestures.** Sources (the app's touch source; the Editor mouse source) call
  `DreamParkGestures.Submit(DreamParkGesture)`; recognition is the source's job, the SDK routes:
  raycast from the camera through the screen point (triggers included; `Ignore Raycast`, `UI` and
  the local rig skipped), then
  - object hooks: `ontap(t)` (Tap) / `ontouch(t)` (TouchDown/Up) on every enabled LuaBehaviour of the
    hit object and its ancestors, nearest first; return `true` to stop the walk;
  - global handlers: `dp.on_tap` / `dp.on_touch` (phase `down`/`up`) / `dp.on_drag`
    (`begin`/`move`/`end`) / `dp.on_pinch` (`t.scale`, multiplicative), pumped like
    `dp.on_peer_join`. Inside a LuaBehaviour the owner defaults to the script.
  - `t = {kind, phase, x, y, dx, dy, scale, fingerId, position, normal, object, distance}` — x/y are
    0..1 from bottom-left; position/normal/object/distance are nil when nothing was hit.
  - `DreamParkGestures.Enabled = false` drops everything. The app sets it whenever the phone's AR view is not on screen or not synced to the park: the map/diorama view, relocalizing or lost tracking. Gestures therefore only reach a game while the guest is looking at the real, aligned park.
- **Editor simulation.** `DreamPark → Simulate Phone Player` (checkmark, EditorPrefs, applied on
  entering Play): `dp.device() == 'phone'`, the Park Simulator spawns each package's Phone Player (or
  no rig when the slot is empty), and the Game-view mouse becomes one finger — click = tap, drag =
  drag, scroll = pinch. Scene-placed rigs outside the Park Simulator are not swapped.
- **Phones are an addition, never a replacement.** Headsets keep hands and colliders; give phone
  players a way in (taps), never the only way in. Sample: `Assets/DreamPark/Samples/Phone/phone_tap_sample.lua.txt`.

## Hand-held objects (Oct 2026)
**Read `HANDHELD.md` before putting anything in a player's hand.** On a headset the hand node is the OpenXR wrist
(+Z toward the fingers, +Y back of the hand, thumb −X on the right hand / +X on the left), so an identity mount is 90° off.
- Author the prop as `Prop/Model` + `Grip` (or `PalmGrip`) + `Muzzle`, plus `GripLeft`/`PalmGripLeft`/`Grip2` when needed.
  The Grip pose IS the hold: pistol, wand, sword/haft/torch/palm/ball per `HANDHELD.md` §5.
  `HandheldKit.Normalize` writes it from any model; `HandheldKit.Validate` and `ValidateRig` must PASS.
- Mount from Lua: `dp.hold(prop, 'right'|'left'|'active'[, hand_node])` (re-mounts when the active hand changes; copies
  under `RemoteRig` hands mount themselves; on a rig with only a `BothHands` tracker a named side mounts on THAT hand via a
  follow node under the rig, not on the active hand — Oct 2026), `dp.hold_two_handed(prop, side, off_hand)`, aim with
  `local pos, fwd = dp.muzzle(prop)`, let go with `dp.release_hold(prop)`.
- Never a −1 scale. The template `… Handheld Pivot`s are re-posed to the Grip pose since Oct 2026 (older game copies
  are not: see `HANDHELD.md` §12). The Editor's hands use the device wrist frame (`dp.editor_hand_frame() == 'device'`),
  but render with `HandheldKit.Render` (both hands) and test both hands on a Quest with a remote player watching. The
  `handheld-convention` pre-upload check (Warning) runs the same rules.

## PlayerHead.Override
`Assets/DreamPark/Scripts/Core/PlayerHead.cs`. A host app can put the player's head somewhere other than `Camera.main`; `GameArea` (zone presence) and `dp.head()` (Lua, PlayerPresence) read it first. Null = `Camera.main`. Content never sets it.

## `dp.local_player_present` and the content camera
`PlayerHead.LocalPlayerAbsent` is set by the host app while the local player is not in the park (diorama, relocalizing, a phone showing the miniature). While absent, content sees a stand-in camera far away at (10000, −10000, 10000) that never renders and rides the real head again once present, so cached references heal. In Lua: `dp.local_player_present()`. `dp.head()` and Lua `Camera.main` return the stand-in while absent; C# `Camera.main` is unchanged. Easy* player detection/chase/target/look and StandardEntity player ranges use it. Default false = the old behaviour. Guard enemies with `if dp.local_player_present and not dp.local_player_present() then idle end`.
