# Hand-held objects: mounting guns, swords, phones and balls in a guest's hand

Read this before you put anything in a player's hand. Rotations look right in the Editor and come out wrong on the
headset, so a mistake here only shows up on a device.

## Quick recipe

1. **Check the prerequisites** (§1): `OculusRuntimeSettings.handSkeletonVersion: 1` (OpenXR) and hand tracking only.
   The Editor's hands now use the device wrist frame too, but the acceptance test is still a Quest (§1).
2. **Read the model's axes.** Run `HandheldKit.Inspect ["<model>","<outDir>"]` and look at the render (+X red, +Y green,
   +Z blue). Note which axis the business end points along (**forward**) and which is the top or thumb side (**up**).
3. **Normalise it.** Run `HandheldKit.Normalize ["<model>","<Prop>.prefab","<forward>","<up>","archetype=pistol|wand|sword|haft|torch|palm|ball"]`.
   The archetype decides the HOLD (§5: guns, wands, swords and axes are each held differently). You get `Prop/Model` (the art, with its axes fixed), `Grip` or `PalmGrip`, and `Muzzle`.
4. **Look** at `HandheldKit.Render ["<Prop>.prefab","both","<outDir>"]`. Tune with `grip=x,y,z` or `muzzle=x,y,z`, or by
   moving the `Grip` child, then render again.
5. **`HandheldKit.Validate`** on the prop must report PASS. Use `ValidateOpts … "muzzle=none"` for props that do not shoot.
6. **Hold it from Lua:** `dp.hold(prop, 'right'|'left'|'active')` (in `start()`, or when the player picks it up).
   It mounts the prop by its Grip for that side (§5 contract), follows the hand in use for `'active'` and re-mounts when
   it changes, and reparents the prop under the local rig's hand if it is not already under a hand node.
   `dp.release_hold(prop)` lets go. Place the remote copy under `RemoteRig/RightHand|LeftHand|Hand` (§3): any prop with a
   `Grip` child there is mounted automatically for that side, with no script.
7. **Builders** that need the pose at edit time (a prefab that should look right before Play) use `HandheldKit.Place` /
   `Mount`, which run the same maths (`HandheldMath`): rotation `Euler(0,0,+90)` right / `Euler(0,0,-90)` left times
   `Inverse(Grip.localRotation)`; position `(∓0.007,-0.033,0.081) − rotation × Grip.localPosition`.
8. **In Lua**, aim with `local pos, fwd = dp.muzzle(prop)`. Never use a negative scale. Recoil goes on `Model`.
9. **`HandheldKit.ValidateRig ["<Player>.prefab"]`** must report PASS. It covers both hands, the remote copies and the
   template pivot trap.
10. **Test on a headset** with both hands. The acceptance test is "it feels like it's in my hand".

The Editor tool is `HandheldKit` (`Normalize`, `Validate`, `ValidateRig`, `Render`; not yet bundled with the SDK). The
runtime is the platform's `dp.hold` / `dp.muzzle`, and the `handheld-convention` pre-upload check runs the same rules. For apps
older than the October 2026 release, `Assets/DreamPark/Samples/Handheld/handheld_mount.lua.txt` (re-mounts when the hand
in use changes) and `two_hand_aim.lua.txt` (two-handed props) implement the same contract in Lua. Where you have the tool, run it through the Unity CLI:
`unity command --project-path . --timeout 600 run_script --file Tools/Builders/Handheld/HandheldKit.cs --entry HandheldKit.<Fn> --args '[…]'`.

> **Why this doc exists.** A playtester on a Quest 3S reported that "the confetti gun is turned 90 degrees so it doesn't
> feel like it's in my hand". The guns had been nested under the hand with identity rotation, so the top pointed out of
> the back of the hand and the handle stuck out of the palm. It looked right in the Editor. Several titles had hit the same
> bug, and the SDK template's `… Handheld Pivot` was wrong in the same way until October 2026 (§12). Everything below has been checked on a device or measured from Meta's own hand data.

---

## 1. Prerequisites: check these before you mount anything

| Check | Where | Expected | What changes otherwise |
|---|---|---|---|
| Hand skeleton | `Assets/Resources/OculusRuntimeSettings.asset` → `handSkeletonVersion` | `1` = **OpenXR**. The DreamPark app, which your content runs in, ships with `1` as of Oct 2026. | `0` (OVR) uses the legacy hand-root axes; the SDK template pivot `(0,-90,-98.6)` dates from that era. Under OVR, none of the numbers in this doc apply. Re-derive them on Meta's `OVRCustomHandPrefab_L/R` and verify on a device. |
| Your project's value | the same file in your game project | `1`, to match the app | It only affects Editor/Link testing. **Your content runs inside the DreamPark app, so the app's value is the one that applies on device.** |
| Input mode | the DreamPark app's `OculusProjectConfig` → `handTrackingSupport` | `2` = **HandsOnly** | DreamPark has no controllers. If a host ever enables them, the hand anchor becomes the **controller** pose: +Z out of the controller's front, +Y up, origin near the grip. A prop authored to this convention then mounts at about identity, not ±90°. `HandTracker` also hides its children whenever `OVRHand.IsTracked` is false, which it is while a controller is held. Do not design for controllers. If you must, branch on `OVRInput.GetActiveController()` and verify on a device. |
| Editor | Scene-view `Simulator`, AI Playtester `PlaytestAvatar` | device wrist frame (Oct 2026) | Both now pose the hand anchors in the device wrist frame (`HandheldMath.EditorAnchorRotation`: a relaxed pistol grip pointing where you look — fingers forward, thumb up, palm inward), so a correct mount looks correct in Play mode; `dp.editor_hand_frame()` returns `'device'`. Older SDKs copied the **head rotation** (+Y up, +Z forward) to both hands, so a correct mount looked rolled 90°; content that compensated for that in Lua must remove the compensation (gate it on `dp.editor_hand_frame() ~= 'device'`). The offline render (§9) and a device test remain the acceptance test. |

---

## 2. The hand frame on the headset

`HandTracker` and the `RemoteRig` hand nodes copy `OVRCameraRig` `LeftHandAnchor` / `RightHandAnchor`. Those anchors are
posed by `OVRInput.GetLocalController{Position,Rotation}(LHand|RHand)`, which is the **OpenXR WRIST joint**. Its axes are
the same for both hands:

| Wrist axis | Right hand | Left hand |
|---|---|---|
| **+Z** | wrist → fingers (the pointing direction) | wrist → fingers |
| **+Y** | **back of the hand** (palm faces −Y) | **back of the hand** (palm faces −Y) |
| **+X** | **pinky side** (thumb = **−X**) | **thumb side** (thumb = **+X**) |

The origin is the wrist. There are two mount targets, in metres, in the wrist frame:

- **Fist centre**: the power-grip axis under the middle finger. Right **(−0.007, −0.033, 0.081)**, left **(+0.007, −0.033, 0.081)**.
- **Palm surface**: the centre of the open palm. **(0, −0.022, 0.064)** for both hands.

Sources:
- The axes were measured from Meta's `OpenXRCustomHandPrefab_L/R`; the thumb tip sits at x = −0.078 on the right hand and
  +0.078 on the left.
- The fist centre is the power grip from Meta's Interaction SDK `Torch.prefab` `HandGrabPose`, posed on that hand.
- The convention was proven on device in several shipped titles (blasters, a two-gun rig, remote players).

Meta's grip axis is not exactly perpendicular to the fingers. It runs from the thumb side to the pinky side, raked about
14° toward the wrist and about 5° toward the back of the hand. A pistol handle raked back by up to about 20° fits it
naturally.

**`HandTracker.flipVisual`** with `flipVisualRotation (180,180,0)` is a 180° roll about +Z. It applies only while the LEFT
anchor drives the tracker. With it enabled, the tracker frame of both hands has the thumb at −X, and the left palm is at
+Y. The template ships with it **off**; some titles turn it on for `BothHands`. Pick one setup per tracker:

| Setup | Thumb side | Palm side | Grip rotation in the hand node |
|---|---|---|---|
| Raw anchor (template default; every `RemoteRig` hand) | R −X / L +X | −Y | R `Euler(0,0,90)` / L `Euler(0,0,-90)` |
| `flipVisual (180,180,0)` (while the left hand drives it) | −X | R −Y / L +Y | `Euler(0,0,90)` for both; the left fist centre becomes **(−0.007, +0.033, 0.081)** |

`HandheldKit.ExpectedGripPose(left, flip, flipRot, flipPos, …)` computes this for any flip values.

---

## 3. Where to put it in Player.prefab

These names come from the SDK template rig (`Assets/Content/Sample/Prefabs/Player.prefab` in an SDK project), checked
against the October 2026 Sample. Games add `HandTouch` and `RemoteRig` themselves; `RemoteRig` is optional.

| Node | Component / driver | Frame on device | Put here |
|---|---|---|---|
| `Player/RightHand` | `HandTracker` (handPreference **Right**, flipVisual off) → `RightHandAnchor` | raw, right | **Right-hand-only** props. Mount with `Euler(0,0,90)` at the right fist centre. |
| `Player/LeftHand` | `HandTracker` (**Left**) → `LeftHandAnchor` | raw, left | **Left-hand-only** props. Mount with `Euler(0,0,-90)` at the left fist centre. |
| `Player/BothHands` | `HandTracker` (**Both**: keeps the hand it has while that hand stays tracked; if both appear at once it keeps the previous hand, otherwise the right) | raw anchor of **whichever hand is in use**, so the frame changes when the hand changes | One prop in **whichever hand is in use**: `dp.hold(prop, 'active')`, which re-mounts it when the hand changes. |
| `…/Right Handheld Pivot (Attach guns, handhelds, etc)` under `BothHands` and `RightHand`; `…/Left Handheld Pivot (…)` under `LeftHand` | plain transform. **October 2026 template: the Grip pose** — right/BothHands `(−0.007, −0.033, 0.081)` euler `(0, 0, 90)`, left `(0.007, −0.033, 0.081)` euler `(0, 0, −90)`. Older templates: `(±0.022, −0.019, 0.1)` euler `(0, −90, −98.6)` | the Grip pose on the raw wrist (old: **wrong**, a barrel points up along the thumb) | A static right- or left-hand prop can hang here at `−Grip.localPosition` (the origin after `Normalize recenter=grip`). Prefer `dp.hold`, which works under any node and both hands. A `Player.prefab` copied from an older template still has the old pose: re-pose it as above. `ValidateRig` flags props on an old pivot. |
| `<Hand>/HandTouch` (added by games that need hand colliders) | dynamic `Rigidbody` + `FixedJoint` to the tracker's kinematic body + a collider, offset (0,0,0.06), no rotation | same as its tracker (the mount is computed in tracker space) | **Colliders** that must push things. Visual props can hang here too, but they lag the hand by a physics step and can be pushed. Prefer to put visuals directly under the tracker, or under a plain child with no Rigidbody (e.g. `Grip`). |
| `RemoteRig/RightHand`, `RemoteRig/LeftHand` | `RemoteRig.Drive` → the peer's **raw** anchors (hidden while untracked) | raw | The **remote copy** of each local prop, in exactly the same pose relative to its node. |
| `RemoteRig/Hand` | the peer's active hand (`dp.peer(peer_id).active_hand`) | raw anchor of that hand | The remote copy of a `BothHands` prop. Mounted automatically and re-mounted when that player switches hands. |
| `RemoteRig/Head` | the peer's head | camera frame | Hats and visors. Not a hand. |

Node names are matched loosely: `RemoteRig`, `Remote Rig` and `remote_rig` all work, as do `LeftHand`, `Left Hand` and
`left_hand`. `PlayerPresence` streams the **physical** anchors, never your rig nodes, so a remote copy never has
`flipVisual` applied.

**Older apps only:** before `dp.hold` (October 2026), **`handheld_mount.lua.txt`** goes on the prop root under an active-hand node. Its `update()` watches
`tracker.isUsingLeftHand` (local) or `dp.peer(peer_id).active_hand` (remote) and re-mounts the prop when the side changes.
It handles `flipVisual`, `GripLeft`, `PalmGrip` and `PalmGripLeft`. The core of it:

```lua
local function mount(side)                      -- side = 'left' | 'right'
    local left = side == 'left'
    local g = grips[side]                         -- {lp, lr, palm} from Grip / GripLeft / PalmGrip / PalmGripLeft
    local grot = UE.Quaternion.Euler(0, 0, left and -90 or 90)
    local gpos = g.palm and UE.Vector3(0, -0.022, 0.064) or UE.Vector3(left and 0.007 or -0.007, -0.033, 0.081)
    if left and tracker ~= nil and tracker.flipVisual then
        local fi = UE.Quaternion.Inverse(UE.Quaternion.Euler(tracker.flipVisualRotation))
        gpos = fi * (gpos - tracker.flipVisualPosition); grot = fi * grot
    end
    local rot = grot * UE.Quaternion.Inverse(g.lr)
    tf.position = node:TransformPoint(gpos - rot * g.lp)
    tf.rotation = node.rotation * rot
end
```

---

## 4. The prop convention

```
<Prop>            root = the object. No hand rotation baked in. Scale 1 (any import scale lives on Model).
├── Model         the art. Import-axis fixes (rotation / scale / offset) live HERE, never on the root.
├── Grip          the prop point that lands on the FIST CENTRE (Meta's power-grip axis under the middle finger).
│                   It is on the handle for most holds. For guns (the high pistol hold) it sits 4.8 cm under and 3.3 cm beside the handle
│                   point, so that the gun rides high. Use the per-archetype values in §5.
│                   Grip +Z = where the FINGERS point. Grip +Y = the THUMB side of the fist.
├── PalmGrip      instead of Grip, for objects resting on the OPEN palm: the contact point, which lands on the palm surface.
├── GripLeft / PalmGripLeft   optional: what the LEFT hand uses instead (see "Mirroring" below)
├── Grip2         optional: the off-hand fist point of a two-handed prop (same axis meaning)
└── Muzzle        barrel tip or emitter. +Z = shot / spray / beam direction. Optional for props that don't shoot.
```

**Mirroring.** The mount is a rotation, never a reflection, so whatever faces the back of the RIGHT hand faces the palm of
the LEFT hand. That is invisible for objects that are symmetric left to right: guns, swords, bats, wands, balls. It matters
for anything with a face toward the palm: a phone screen, a shield, a watch face, a tray, a ball's contact point, a printed
paddle. For those, add `GripLeft` / `PalmGripLeft`, normally at the same position with the grip **rolled 180° about its
+Z**. `Normalize archetype=palm|ball` does this for you.

A bow's arrow-shelf side cannot be mirrored by any rotation. Ship a mirrored mesh (re-exported, not a −1 scale) or keep the
shelf central.

---

## 5. Grip archetypes: the hold is data in the Grip

Guns, wands, swords and axes are all held differently. The mount math never changes:
`rot = HandFrame(side) × Inverse(Grip.localRotation)`, and the Grip lands on the fist centre. So **each hold is just a
Grip pose on the prop**: where the Grip sits, and how it is rotated. `HandheldKit.Normalize archetype=…` writes these
values for you. Author them by hand only when you know better (and re-render).

**Source of each hold.** *Device-proven* means it was held on a Quest and approved. *Derived* means it was computed
from Meta's hand data and has not yet been confirmed on a device.

| Archetype (`archetype=`) | Examples | Author the prop (forward / up) | Grip position | Grip rotation (Unity euler) | Left hand | Source |
|---|---|---|---|---|---|---|
| **Gun / pistol grip** (`pistol`) | blaster, confetti gun, spray can, drill, hair dryer | +Z barrel, +Y top of gun | handle point **P** (1.5 cm under where the handle meets the body, centred on the lower half of the handle) **+ (−0.033, −0.048, +0.012)** | identity: barrel exactly along the fingers, top exactly on the thumb side | `GripLeft` = P **+ (+0.033, −0.048, +0.012)** | **Device-proven** on a shipped blaster (2026-10-05). The fist rides the **lower** handle and the gun body sits high: P is 4.8 cm up the thumb side and 3.3 cm toward the back of the hand from Meta's fist centre. The left hand is that title's own flipVisual mirror, exactly. |
| **Wand** (`wand`) | magic wand, conductor's baton, pointer, light stick | +Z shaft (tip), +Y the decorated "top" of the handle, butt at the back | on the shaft **5.8 cm from the butt**, offset so the shaft passes **1.1 cm** toward the back of the hand: Grip = butt + **(−0.011, +0.001, +0.058)** | **(24.2, 9.5, 3.9)**: the shaft leans **24.7° toward the thumb side** and **7.9° toward the palm** off the pointing axis, held like a real wand or pencil, not straight out | same Grip (the source title's left hand matches within 6 mm and 0°) | **Device-proven** on a shipped wand title (HandTracker Both + flipVisual (0,0,180)/(0,−0.06,0)). |
| **Sword / one-handed blade** (`sword`) | sword, sabre, dagger, knife, lightsaber | +Z edge, +Y blade tip | on the hilt axis, **2.5 cm under the guard** (the index finger sits under the guard; the guard is found automatically as the widest slab along Z in the lower 60 %) | right **(−13.9, 0, −5.7)**: the blade comes out of the thumb side, raked **14° toward the fingers** and 5.5° toward the palm. That is the handle running through the fist. The wrist's natural angle then points the blade forward. | `GripLeft` **(−13.9, 0, +5.7)** | **Derived: Meta Interaction SDK power grip** (`Torch.prefab` `HandGrabPose`). |
| **Axe / hammer / bat / club** (`haft`) | axe, hammer, mallet, bat, club, golf club, pickaxe | +Z striking face (blade edge, hammer face), +Y head | on the haft axis **5.5 cm above the bottom end** (pommel or knob just under the little finger; choke up with `grip=`) | same as sword: R **(−13.9, 0, −5.7)** | `GripLeft` **(−13.9, 0, +5.7)** | **Derived: Meta power grip.** |
| **Torch / handle** (`torch`) | flashlight, torch, microphone, bottle, ice-cream cone, umbrella | +Z front, +Y top (light/flame end) | on the handle axis at 40 % of the height (4.5–10 cm above the bottom) | same as sword: R **(−13.9, 0, −5.7)** | `GripLeft` **(−13.9, 0, +5.7)** | **Derived: Meta power grip** (this is literally Meta's torch). Muzzle at the top, +Z = up the handle. |
| **Racket / foil / paddle** (handshake grip; use `torch` or `haft`, then add a tilt) | tennis or ping-pong racket, fencing foil | as torch | as torch | `torch` rotation × **Euler(−45,0,0)**: the tip tilts a further 45° toward the fingers | same for GripLeft | **Derived.** Render it and test on a device. |
| **Reverse / ice-pick** (author it by hand) | dagger held overhand, ski pole from the top | as sword | as sword | sword rotation × **Euler(0,0,180)**: the tip comes out of the pinky side | as sword | **Derived.** `Validate` warns, as intended. |
| **Flat on the palm** (`palm`) | phone, tray, plate, book, card | +Z toward the fingertips (top edge), +Y the face away from the palm (screen) | **`PalmGrip`** on the back face, 0–3 cm toward the bottom edge → lands on the palm surface (0, −0.022, 0.064) | `PalmGrip` **(0, 0, −90)** | `PalmGripLeft` **(0, 0, +90)** | **Derived** (open-hand render). |
| **Ball** (`ball`) | ball, apple, orb, snowball | any | **`PalmGrip`** = contact point on the prop's **+X** side | identity | `PalmGripLeft` on the **−X** side | **Derived.** |
| **Pinch** (author it by hand) | coin, card edge-on, key, pencil | any | where the fingertips meet; lands near (∓0.03, −0.04, 0.14) | identity | mirror x | **Rough.** Following the fingertips needs fingertip bones (`OVRSkeleton`), which is an app feature. |
| **Bow** (`torch`, bow hand) | bow | +Z toward the target, +Y upper limb | riser handle, as torch | as torch | as torch | **Derived.** Arrow-shelf side: see §4 Mirroring. |
| **Two-handed** | rifle with a foregrip, staff, bat, steering wheel, guitar | the main archetype's axes | main `Grip` as its archetype, plus **`Grip2`** where the off hand holds it (a foregrip uses the pistol axes) | as its archetype | as its archetype | `dp.hold_two_handed(prop, side, off_hand)`: Grip → Grip2 is aimed at the off fist while the off hand is tracked (`two_hand_aim.lua.txt` on older apps). |

**What the numbers mean in the hand** (raw wrist frame, right hand; the left hand is the mirror):
- **gun**: barrel = (0, 0, 1); top = thumb (−1, 0, 0); handle point at (−0.055, 0.000, 0.069).
- **wand**: shaft = (−0.414, −0.137, 0.900); tip about 39 cm forward and 14.5 cm toward the thumb.
- **sword / axe / torch**: tip = (−0.966, −0.096, 0.241), Meta's power-grip axis.

`HandheldKit.SelfTest` asserts every one of these in both hands (§10).

**Two-handed props.** `dp.hold_two_handed(prop, 'right', offHandNode)` mounts the prop in the main hand and, while the
off hand (`Player/LeftHand`, or `RemoteRig/LeftHand` on the remote copy) is tracked, pivots it about the main fist so
that `Grip → Grip2` points at the off fist, keeping the roll from the main hand's thumb. While the off hand is
untracked the prop goes back to the one-hand pose. Use per-side nodes, not `BothHands`. On older apps put
`two_hand_aim.lua.txt` on the prop root with the `offHand` GameObject injection instead.

**Verifying an archetype.** `HandheldKit.Render` mounts the prop on Meta's hand, posed in a **power grip** for `Grip` or as
an **open hand** for `PalmGrip`. Look for:
- **Gun:** the fist rides the lower handle and the body sits above the knuckles.
- **Wand:** the shaft runs diagonally out between thumb and index, leaning toward the thumb.
- **Sword / axe:** the handle goes through the fist and the head or blade sits above the thumb.
- **Palm:** the face of the prop is visible from the palm side and hidden from the back of the hand.


### `dp.hold`: the platform API reads these Grips

**`dp.hold(prop, 'left'|'right'|'active'[, hand_node])`** is a pure-Lua call (no components). It reads the prop's Grip,
so **authoring the Grip per the table above is the whole job**. It implements exactly this contract
(`Assets/DreamPark/Scripts/Features/Player/Handhelds.cs` + `HandheldMath.cs`), and `HandheldKit.ValidateRig` checks the
same thing on a prefab:
1. **Pick the hand node.** `left` → `Player/LeftHand`, `right` → `Player/RightHand`, `active` → the hand in use, re-evaluated
   every frame. On a rig with only `Player/BothHands`, `left`/`right` mount on a follow node under the
   rig (`Handheld Follow (left|right)`) that copies that hand's anchor (`dp.hand(side)`) every LateUpdate — the raw wrist,
   so no flip — instead of on `BothHands`, which follows the hand in use (Oct 2026; before, the prop rode the active hand). On a RemoteRig clone, use `RemoteRig/LeftHand`, `RemoteRig/RightHand` or `RemoteRig/Hand` and
   `dp.peer(peer_id).active_hand`.
2. **Pick the marker.** The left hand uses `GripLeft`, then `PalmGripLeft`, then `Grip`, then `PalmGrip`. The right hand uses
   `Grip`, then `PalmGrip`. A `Palm*` marker targets the palm surface (0, −0.022, 0.064); otherwise the target is the fist
   centre (∓0.007, −0.033, 0.081).
3. **Compute the pose.** `rot = Euler(0, 0, left ? −90 : 90) × Inverse(marker.localRotation)` and
   `pos = target − rot × marker.localPosition`, in the hand node's raw-anchor frame. If the node is a `HandTracker` with
   `flipVisual` and the left anchor is driving it, pre-apply `Inverse(flipRot)` and subtract `flipPos`. A rig tuned on
   device to another point can override the target per prop with `Handhelds.SetTarget(prop, rightHandPoint)` (C#; the
   left hand mirrors x).
4. **Pure rotation.** Never apply a negative scale. Re-pose only when the side changes.
5. **Remote copies.** A prop with a `Grip` child directly under a RemoteRig clone's `LeftHand` / `RightHand` / `Hand` is
   mounted automatically for that node's side (`Hand` follows the player's live active hand). No script on the remote copy.
   This runs as soon as a clone exists (Oct 11 2026; before, only once the title had called `dp.hold` or `dp.net_body`, so
   remote guns could stay in the right-hand pose). Only a **direct** `Grip*` child counts, so a container such as
   `Hand/Grip` is left to its own script. `dp.hold` from a clone's script always uses that clone's hands: `active`
   → `Hand`, or, with no `Hand` node, `LeftHand`/`RightHand`, moving between them when the player switches hands.

On apps older than the October 2026 release, `handheld_mount.lua.txt` implements the same contract in Lua.
`HandheldKit.LuaEditTest` verifies it in the real XLua VM.

---

## 6. Normalising any model (FBX / GLB / Blender / Meshy / Asset Store)

Imported art can arrive in any orientation: Y-up or Z-up, the barrel along ±X, ±Y or ±Z, scaled ×100 (exported in cm),
with the pivot anywhere.

1. **Import** it into your content folder as usual. Do not rotate it in the FBX import settings to fix it; `Model` handles
   that.
2. **Detect.** `HandheldKit.Detect ["<model>"]` prints the bounds. For **gun-shaped** objects it also prints a guess of
   forward and up, with a confidence value.
   - How the guess works: forward is the longest axis and up is the second longest. Of the two ends of the up axis, the one
     whose slab is narrow along forward is the handle, so that end is DOWN. The barrel sticks out in front of the handle.
   - It was right on all three gun test copies (§10), but it is **only a guess**. Ignore it for swords, phones and balls,
     which are not gun-shaped.
3. **Inspect.** `HandheldKit.Inspect ["<model>","<outDir>"]` renders the raw model from +X, −X, +Y, +Z, −Z and an iso view,
   with axis rods (+X red, +Y green, +Z blue; the sphere marks the + end). **Look at it** and write down:
   - **forward**: the axis the business end points along (pistol barrel; sword edge; phone top edge);
   - **up**: the top or thumb side (gun top; sword tip; phone screen; for a ball, any axis).
4. **Normalise.** Run `HandheldKit.Normalize ["<model>","<out>.prefab","<forward>","<up>","<opts>"]`.

   Opts are separated by `;`:
   - `archetype=pistol|wand|sword|haft|torch|palm|ball|center` (§5);
   - `scale=auto|<factor>|len:<metres along forward>`: `auto` applies 0.01 when the model is larger than 3 m, and 100 when
     it is under 1 cm;
   - `grip=auto|x,y,z`;
   - `muzzle=auto|art|none|x,y,z`: `art` uses a `Muzzle` the art already has;
   - `recenter=grip|none`: the default, `grip`, puts the prop's origin on the Grip.

   What it does:
   - wraps the art in `Model`;
   - rotates `Model` so that forward → +Z and up → +Y;
   - fixes the scale;
   - places `Grip` / `PalmGrip` (plus `PalmGripLeft`) and `Muzzle` according to the archetype;
   - recentres;
   - saves the prefab and runs `Validate`.

   On an existing handheld prefab that already has a `Model`, it fixes the prefab in place and does not recentre.
5. **Render and tune.** Run `HandheldKit.Render ["<out>.prefab","both","<outDir>"]`. If the grip is too high or too low,
   re-run with `grip=x,y,z` (in the normalised frame, before recentring), or move the `Grip` child, and render again.
6. **Validate.** `HandheldKit.Validate` / `ValidateOpts` must report PASS (§9).

A mirrored export (a negative scale anywhere) cannot be fixed by a rotation: `Normalize` warns and `Validate` fails.
Re-export it unmirrored.

---

## 7. The mounting recipe (builder code)

At runtime, call `dp.hold` (§5). This is the same maths for **edit-time** builders, so a prefab already looks right
before Play (`HandheldMath.Mount` in the SDK does it for you):

```csharp
// Raw anchor frame: +Z fingers, +Y back of hand, thumb = -X (right) / +X (left).
static readonly Vector3 FistCentreRight = new Vector3(-0.007f, -0.033f, 0.081f);
static readonly Vector3 PalmSurface     = new Vector3(0f, -0.022f, 0.064f);
static Vector3 FistCentre(bool left) => left ? new Vector3(0.007f, -0.033f, 0.081f) : FistCentreRight;
static Quaternion HandFrame(bool left) => Quaternion.Euler(0f, 0f, left ? -90f : 90f);   // Grip +Z -> fingers, +Y -> thumb

/// handNode: the transform that follows the anchor (HandTracker object or RemoteRig/<Side>Hand).
static void Place(Transform handNode, Transform prop, bool left)
{
    // LEFT prefers GripLeft / PalmGripLeft; PalmGrip* lands on the palm surface, Grip* on the fist centre
    Transform g = null; bool palm = false;
    foreach (var n in left ? new[] { "GripLeft", "PalmGripLeft", "Grip", "PalmGrip" } : new[] { "Grip", "PalmGrip" })
        if ((g = prop.Find(n)) != null) { palm = n.StartsWith("Palm"); break; }
    Quaternion rot = HandFrame(left) * Quaternion.Inverse(g ? g.localRotation : Quaternion.identity);
    Vector3 pos = (palm ? PalmSurface : FistCentre(left)) - rot * (g ? g.localPosition : Vector3.zero);
    prop.SetPositionAndRotation(handNode.TransformPoint(pos), handNode.rotation * rot);   // works under HandTouch too
}
```

- `HandheldKit.Place` and `Mount(handNode, parent, prefab, left, flip)` are this same code, plus `flipVisual` support.
  Builders that run on their own through `run_script --file` copy the snippet instead.
- Mount **both hands** and **both copies**: the local one and the RemoteRig one.
- Keep the hierarchy names that scripts look up. The mount changes only the prop root's transform.

---

## 8. Aiming from Lua

- Aim with the **Muzzle**: `local origin, dir = dp.muzzle(prop)` (or `muzzle.position` / `muzzle.forward`). Never use the
  hand's forward, the camera's forward, or `Vector3(0,0,1)`.
- Read the Muzzle on every shot. Do not cache its pose in `awake()`, because nothing has been posed yet at that point.
- Recoil, kick and spin go on `Model`, in the prop's own axes. For example, `Euler(-14,0,0)` tips the barrel toward the
  thumb. The mount owns the root.
- Particle systems that fire forward live under `Muzzle` and emit along its +Z.

---

## 9. Verification checklist (do all of it, for both hands)

1. **`HandheldKit.Validate`** on the prop must report PASS.

   It **fails** on:
   - a missing Grip or PalmGrip, or one that is not a direct child of the root;
   - a missing Muzzle (unless you pass `muzzle=none`);
   - a negative scale anywhere;
   - scaled markers;
   - a size over 1.5 m (an import-scale bug);
   - a Grip more than 2 cm outside the mesh bounds;
   - a Muzzle that is not at the far end of the mesh along its own +Z, or that sits beside the front of the mesh;
   - a Grip in front of the Muzzle;
   - a barrel below the Grip on the thumb axis (the prop is upside down).

   It **warns** on:
   - a PalmGrip without a PalmGripLeft;
   - a reverse grip;
   - a shot direction that is neither along the fingers nor out of the thumb side;
   - a gun whose shape does not read as +Z forward / +Y up.

2. **`HandheldKit.Render`** with both hands. **Look** at all six views.

   The pre-upload checks run the same convention on every prop in your content (`handheld-convention`, a Warning):
   fix what it reports, or explain it.

3. **`HandheldKit.ValidateRig`** on `Player.prefab` must report PASS. It walks every prop (anything with a Grip, PalmGrip or
   Muzzle child) under every `HandTracker` and `RemoteRig` hand.

   It **fails** on:
   - a grip rotated more than 10° from the device pose for that side and frame (flipVisual included);
   - a grip more than 2 cm from the fist centre or palm surface;
   - a negative scale;
   - a missing Grip;
   - a remote copy that differs from its local copy by more than 5 mm or 2°;
   - a prop hung on the template pivot (the message names the pivot).

   It **warns** when a prop on an active-hand node has no `handheld_mount` script. A prop your Lua holds with
   `dp.hold(prop, 'active')` at runtime needs neither; the warning only describes the static prefab.

4. **Play mode** (under the play lock): Lua errors, the right prop showing, the muzzle firing, and — now that the Editor
   hands use the device wrist frame — a first look at the pose. It does not replace the render or the device test.

5. **Device:** test both hands while pointing at something, and also have a remote player watch you.

The Lua helpers are checked in the real XLua VM, with no Play mode needed:
`HandheldKit.LuaEditTest ["<handheld_mount.lua.txt>","<two_hand_aim.lua.txt>"]`.

---

## 10. What the kit was proven on (`HandheldKit.SelfTest`, 2026-10-07: 52/52 PASS)

| Case | Mis-orientation | Result |
|---|---|---|
| A: confetti gun (`pistol`) | Blender Z-up (barrel −Y, top +Z), exported in cm (×100), pivot 11 cm off | Detect correct; the raw copy FAILS Validate; Normalize ×0.01 → PASS; barrel along the fingers and top on the thumb side in both hands; ValidateRig PASS |
| B: party blaster (`pistol`) | barrel −X, top −Z, pivot offset | same as A |
| C: a shipped blaster (`pistol`) | native mesh frame (barrel +X, top +Z) | same as A, **and** the normalised mesh lands exactly on its device-verified pose in both hands (bounds centre (∓0.075, 0, 0.115)) |
| D: sword (`sword`) | lying down: blade +X, edge −Y | blade along +Y; tip along Meta's power-grip axis in both hands; PASS |
| E: phone (`palm`) | standing: top edge +Y, screen −Z | the screen faces out of BOTH palms; PASS |
| F: ball (`ball`) | pivot off-centre | contact on the palm in both hands; PASS |
| G: wand (`wand`) | lying along −Y, gem +X | shaft = the device-proven wand (−0.414, −0.137, 0.900) right and (0.414, 0.137, 0.900) left; PASS |
| H: axe (`haft`) | upside down: head −Y, blade −Z | head along +Y; Meta power-grip axis in both hands; PASS |
| Rigs | a multi-gun Player.prefab (guns on the pistol hold) | PASS: 8 props, local and remote, Grip/GripLeft |
| | the old identity mount (the device bug) | FAILS: 90° off |
| | a remote copy moved by 3 cm | FAILS: remote ≠ local |
| | a prop hung on the template `Handheld Pivot` | FAILS: 174° / 90° off; the pivot is named |
| Lua (`LuaEditTest`) | `handheld_mount`: BothHands R/L, + flipVisual R/L, RemoteRig/Hand R/L, PalmGrip R/L; `two_hand_aim`: tracked and lost | 10/10 PASS, 0.00 mm / 0.00° |

---

## 11. Common mistakes

| Mistake | Symptom on device | Fix |
|---|---|---|
| Identity rotation under the hand ("+Y is up") | Rolled 90°: the top points out of the back of the hand and the handle sticks out of the palm | §7 |
| The same rotation for both hands on raw anchors | One hand is correct and the other is upside down | Left = `Euler(0,0,-90)` |
| Mirroring with `localScale.x = -1` | Colliders and particle shapes break, normals and lighting flip, `transform.right` flips in Lua | A rotation per side; `GripLeft` for palm-side faces |
| Hanging props on an OLD template `… Handheld Pivot` (`(0,−90,−98.6)`, before Oct 2026) | The barrel points up along the thumb (right hand) or down (left hand) | Re-pose it (§12) or use `dp.hold` |
| Judging the mount in an old SDK's Editor (hands copied the head rotation) | Perfect in the Editor, 90° off on the headset | Update the SDK; render it (§9) |
| Fixing import axes on the prop root or in FBX settings | Every later mount inherits the error | Fix them on `Model` (`Normalize`) |
| Grip at the bounding-box centre | The prop floats above or below the fist | Use the archetype values (§5) |
| Gun Grip on the handle under the middle finger (the Meta fist) | The gun sits about 5 cm low, buried in the fist; the device-proven hold rides higher | `archetype=pistol`: handle point + (∓0.033, −0.048, 0.012) |
| Wand mounted straight along the fingers | It feels like a stick strapped to the hand | `archetype=wand`: the 24.7° lean toward the thumb |
| Sword/axe Grip copied from a gun | The blade points along the fingers like a bayonet | `archetype=sword`/`haft`: the blade comes out of the thumb side, raked 14° |
| PalmGrip without PalmGripLeft | The phone screen faces into the left palm | `archetype=palm` adds PalmGripLeft |
| Aiming with the hand's forward | Shots leave from the wrist at an angle to the barrel | Use `Muzzle.forward` |
| A prop in the hand in use that nobody holds with `dp.hold(prop, 'active')` | Correct in the right hand, upside down in the left | §3, §5 |
| Mixing `flipVisual` with per-side rotations | The left hand is off by 180° | Pick one setup (§2) |
| A remote copy posed differently from the local one | Other players see your gun in the wrong place | `ValidateRig` checks remote against local |
| A visual prop on a jointed `HandTouch` | It lags the hand and gets pushed by the world | Visuals under the tracker, colliders on HandTouch |

---

## 12. The SDK template pivot (for SDK maintainers)

The template `Player.prefab` (SDK `Content/Sample` and `Content/YOUR_GAME_HERE`) ships `Right Handheld Pivot (Attach
guns, handhelds, etc)` (under `BothHands` and `RightHand`) and `Left Handheld Pivot (…)` (under `LeftHand`).

- **Before October 2026** both sat at `(±0.022, −0.019, 0.1)` with euler `(0, −90, −98.6)`. In the OpenXR wrist frame an
  object authored +Z forward / +Y up and hung there points its **barrel along −X**: up along the thumb on the right
  hand, down on the left, with its top along the fingers (`HandheldKit.SelfTest` reproduces it).
- **October 2026: re-posed to the Grip pose, names kept** (so existing references still resolve):
  - `Right Handheld Pivot` (under `BothHands` and `RightHand`) → `(−0.007, −0.033, 0.081)`, euler `(0, 0, 90)`;
  - `Left Handheld Pivot` → `(0.007, −0.033, 0.081)`, euler `(0, 0, −90)`.

  A prop hangs at `−Grip.localPosition`, which is the origin when it was normalised with `recenter=grip`.
- The `BothHands` pivot is right-hand posed; a prop held in whichever hand is in use calls `dp.hold(prop, 'active')`
  (`handheld_mount.lua.txt` on older apps).
- Game projects created from an older template keep the old pose until someone re-poses it (`ValidateRig` names it).
- Shipped with the SDK: `HANDHELD.md` (docs), `Assets/DreamPark/Samples/Handheld/handheld_mount.lua.txt` and
  `two_hand_aim.lua.txt` (fallbacks for older apps), the `handheld-convention` pre-upload Warning. Not yet bundled: the `HandheldKit` Editor tool.
