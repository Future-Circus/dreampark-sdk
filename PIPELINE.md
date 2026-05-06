# DreamPark SDK — Unity Mixed Reality Game Pipeline

End-to-end ordered checklist for shipping a mixed-reality experience on Meta Quest 3S using the **dreampark-sdk** (Unity 6000.0.39f1, URP, OpenXR, Meta XR SDK 81, Addressables, XLua).

The paradigm in one paragraph: a creator clones `dreampark-sdk`, runs `new-park.sh` to rename `Assets/Content/YOUR_GAME_HERE/` to a `PascalCase` park name (e.g. `CoinCollector`), authors the experience as one or more **Attractions** (rooms / activities) populated with **Props** (interactive objects). The headset's `Camera.main` position drives a `GameArea` enter/exit check on every Attraction; the matching `PlayerRig` and `DreamBand` (wrist UI) `Show()` while inside, `Hide()` outside. **All gameplay logic lives in Lua scripts** attached to GameObjects via the `LuaBehaviour` component (XLua) — never write C# for gameplay. Final output is automatically bundled via Addressables and deployed to DreamPark servers via `DreamPark → Content Uploader`.

### Interaction model — read this first

DreamPark mixed-reality interactions are intentionally limited to three patterns. Do not introduce hand-gesture menus, pokeable buttons, ray pointers, locomotion sticks, or Meta Interaction SDK widgets — they are too complex for the audience and break the room-scale fantasy.

The three sanctioned patterns are:

- **Space triggers** — the player's body enters or exits a volume. Implemented by `GameArea` (Attraction-scale, drives PlayerRig swap) and by trigger colliders on props (prop-scale, drives `ontriggerenter` in Lua).
- **Broad collisions** — the player's hand / head / foot / body Rigidbody hits a prop collider, or a prop hits another prop. Implemented by `oncollisionenter` / `ontriggerenter` in Lua.
- **Item-based interactions** — the player picks up a physical prop and uses it to act on the world (a hammer hits a peg, a coin lands in a slot). Implemented by Rigidbody physics + collision callbacks in Lua. Grab is provided by the SDK's hand colliders interacting with prop Rigidbodies — there is no abstract "grab event."

Anything more elaborate must be expressed by combining these three.

---

## Phase 0 — Planning & Pre-Production

1. Define the **park concept**: one paragraph describing the experience and its core fantasy ("the player walks into a closet and is teleported into a coin-collecting arcade").
2. Decide the **GameLevelSize** target: `Micro` (14×16 ft), `Boutique` (16×30), `Small` (30×64), `Square` (40×50), `Medium` (50×94), `Large` (80×128), `Jumbo` (120×150), `MallCorridor` (30×260), or `Custom` (Vector2 ft). This is the physical room footprint your attraction expects.
3. Inventory the **attractions** (one per discrete activity — boss fight, mini-game, lobby) and the **props** within each.
4. Categorize each prop by `PropCategory`: `Generic`, `Coin`, `Block`, `Hazard`, `Decoration`, or `Custom`.
5. Sketch every **interaction** and force it into one of the three sanctioned patterns: space trigger, broad collision, or item-based. If you cannot, redesign — do not invent a fourth.
6. List **state variables** (score, lives, timer, current wave). Decide which live as a global `LuaBehaviour` on the `Player.prefab` vs. local on individual props.
7. Identify **multiplayer surface**: which props need `NetId` (synchronized state) and which are local-only.
8. Pick **art direction**: stylized PBR vs. unlit cartoon vs. mixed. Default URP setup with `Assets/DreamPark/Materials/Occlusion.mat` provides the passthrough-cutout floor — your assets must read against real-world lighting.
9. Draft a **scene-flow diagram**: starting attraction, transitions between attractions, end state.
10. Write a **risk list**: anything that needs prototyping before content production (novel collision shape, IK character, custom shader).

## Phase 1 — Project Bootstrap

1. Verify Unity Hub has **Unity 6000.0.39f1** with the **Android Build Support** module (includes OpenJDK + Android SDK + NDK).
2. Clone the SDK: `git clone <dreampark-sdk-url> MyPark` then `cd MyPark`.
3. Run `Tools/new-park.sh MyParkName` — renames `Assets/Content/YOUR_GAME_HERE/` to `Assets/Content/MyParkName/` and rewrites every `gameId: YOUR_GAME_HERE` reference inside prefabs and scenes to `MyParkName`.
4. Open the project in Unity. Wait for the package resolver to finish (`com.meta.xr.sdk.core 81.0.0`, `com.unity.xr.oculus 4.5.0`, `com.unity.addressables 2.3.16`, etc., listed in `Packages/manifest.json`).
5. **Edit → Project Settings → Player → Android → Other Settings**: confirm `Minimum API Level = 32`, `Target API Level = 34`, `Scripting Backend = IL2CPP`, `Target Architectures = ARM64`.
6. **Edit → Project Settings → XR Plug-in Management → Android tab**: enable **OpenXR** and **Oculus**. Under **OpenXR → Feature Groups**, enable **Meta Quest Support**, **Hand Tracking Subsystem**, **Meta XR Foundation**.
7. **File → Build Profiles**: set **Android** as the active platform, click **Switch Platform**. **Texture Compression = ASTC**.
8. Run `DreamPark → Sync Tags & Layers from Core` so the layer indices in `ProjectSettings/TagManager.asset` line up with the SDK: `ARMesh=3, Water=4, Level=6, Entity=7, Item=8, Triggers=10, Player=11, Projectile=12, Spell=14, Portal=16, Enemy=17`.
9. Run `DreamPark → Check for SDK Updates` and accept any updates.
10. Open `Assets/Content/MyParkName/1. Scenes/Template.unity` — this scene is your editing scene; it contains the **[BuildingBlock] Camera Rig**, **Hand Tracking left/right**, **Passthrough**, and **Occlusion** Meta building blocks pre-wired. Do not delete these.
11. Hit **Play** with the Quest 3S connected via Link / Air-Link. Confirm passthrough renders, hands track, and the on-screen FPS counter (`OptimizedAF`) shows ≥ 72 fps in an empty scene.

## Phase 2 — Raw Asset Import

This phase brings raw art into the project. **Do not** start composing prefabs yet — finish all of Phase 2, 3, and 4 on a per-asset basis before combining them in Phase 5+.

1. Source or author 3D meshes in **Blender / Maya / 3ds Max**. Export `.fbx` with: **Apply Transform**, **+Y up / +Z forward**, **scale 1.0 (1 Unity unit = 1 meter)**, triangulated, with **smoothing groups**.
2. Drop `.fbx` files into `Assets/Content/MyParkName/Models/` (game-specific) or `Assets/Content/MyParkName/ThirdParty/<vendor>/Models/` (licensed packs you actually ship).
3. Select each model → **Inspector → Model tab**: **Read/Write** off (unless a Lua script needs runtime mesh access), **Generate Colliders** off (you will add specific colliders manually in Phase 3), **Mesh Compression = Medium**, **Optimize Mesh = Everything**.
4. **Rig tab** (animated characters): **Animation Type = Humanoid** for biped characters (auto-creates Avatar) or **Generic** for everything else; pick the root bone for Generic.
5. **Animation tab**: split the imported take into named clips (`Idle`, `Walk_Loop`, `Jump`, `Hit_React`); set **Loop Time** on cyclic clips; bake **Root Motion** only if the character self-locomotes.
6. **Materials tab**: pick **Use External Materials (Legacy)** the first time, then re-pack inside the prefab — this lets you swap to URP shaders.
7. Run `Assets → DreamPark/Convert to Universal Shader` on any selected non-URP materials to remap to URP/Lit.
8. Drop **textures** (PNG, TIFF, EXR) into `Assets/Content/MyParkName/Textures/`. Inspector defaults: **Texture Type = Default** (sRGB) for albedo, **Normal Map** (linear) for normals, **Single Channel** for masks. **Max Size = 1024** for props, 2048 only for hero assets, **Compression = High Quality**, **Format = ASTC 6×6** for Android.
9. Author **materials** under `Assets/Content/MyParkName/Materials/` (right-click → Create → Material). Use **Universal Render Pipeline / Lit** for opaque, **/Unlit** for stylized, **/Particles/Lit** for VFX.
10. Drop **audio** into `Assets/Content/MyParkName/Audio/`. SFX: `.wav`, **Load Type = Decompress On Load**, **Compression Format = Vorbis**, **Quality = 70**, **Force To Mono** if it's a 3D sound. Music: `.ogg`, **Load Type = Streaming**, **Compression = Vorbis**, stereo.
11. Build **VFX** in `Assets/Content/MyParkName/VFX/` using **Visual Effect Graph** (Window → Visual Effects → Visual Effect Graph) for GPU particles, or **Particle System** components for cheap CPU effects.
12. Anything imported from a Unity package goes into `Assets/Content/MyParkName/ThirdPartyLocal/` first (gitignored, scratch). Run `Assets → DreamPark/Move to ThirdPartyLocal` if it landed elsewhere. After confirming you actually use it, run `DreamPark → Manage Third Party Assets` to copy only the used files into `Assets/Content/MyParkName/ThirdParty/`.

## Phase 3 — Per-Asset Physics, Colliders, Layers, Tags

Every asset must be physics-ready **before** it becomes a prop or part of a level. This is what unlocks the three interaction patterns.

1. Drag the imported model into an empty scene (or an isolated authoring scene under `Assets/Content/MyParkName/Scratch/`) so you can iterate in isolation.
2. **Add a Collider** that reflects how the player's body / hand / foot will touch this object:

   - **Box Collider** for boxy items (coins, bricks, crates).
   - **Sphere Collider** for round items (balls, fruit).
   - **Capsule Collider** for characters / pillars.
   - **Mesh Collider** with **Convex = on** for irregular Rigidbody shapes (skull, statue).
   - **Mesh Collider** with **Convex = off** for static, non-physics geometry (terrain, rooftop). Static-only — never on a Rigidbody.
3. Decide **Trigger vs. Solid**:

   - **Solid collider** (Is Trigger = off) — physical contact, rebounds, blocks the player. Use for items the player can hit, throw, or stand on. Fires `oncollisionenter` in Lua.
   - **Trigger collider** (Is Trigger = on) — passes through but reports overlap. Use for collectible zones, danger volumes, "step here" plates. Fires `ontriggerenter` in Lua.
4. **Add a Rigidbody** if the object should respond to physics or be grabbable: **Mass** (kg, ~0.2 for a coin, ~5 for a brick), **Drag = 0.5**, **Angular Drag = 1**, **Use Gravity** as appropriate, **Interpolation = Interpolate**, **Collision Detection = Continuous Dynamic** for fast-moving objects, **Continuous** for medium, **Discrete** for slow / stationary.
5. **Set the Layer** from the SDK roster (Edit → Project Settings → Tags and Layers):

   - `Item` (8) — anything the player can grab, throw, or push around.
   - `ItemNonInteractor` (9) — items that are physical but not pickup-able.
   - `Triggers` (10) — pure trigger volumes that should not affect physics.
   - `Level` (6) — the floor / static room geometry.
   - `Entity` (7) — characters, NPCs, enemies that aren't pickup items.
   - `Projectile` (12) — items spawned in flight.
   - `Enemy` (17) — hostile characters.
   - `Spell` (14) — magical effect colliders.
   - `Portal` (16) — transition zones.
   - `Player` (11) — reserved for the PlayerRig; don't apply to your assets.
6. **Set the Tag** to the SDK-defined domain tag the rest of the SDK and your Lua scripts will compare against: `Coin`, `Brick`, `Block`, `Lava`, `Mole`, `Projectile`, `Cannon`, `Fruit`, `Ball`, `Bumper`, `Fire`, `Air`, `Rune`, `Spell`, `EnemyAttack`, `FloatingObjects`, `Ground`, `Stone`, `Explosive`, `Pengo`, `Fence`, `Mortar`, `ActiveHit`, `Skip`, `Kicker`, `FxTemporaire`, `GroundJubbo`, `FlyingJubbo`. Full list lives in `ProjectSettings/TagManager.asset`. If none fit, leave as `Untagged` — do not invent new tags casually; they are how Lua scripts identify what hit them.
7. **Verify the Layer Collision Matrix** (Edit → Project Settings → Physics → Layer Collision Matrix) does what you expect: `Player` ↔ `Level`, `Player` ↔ `Item`, `Triggers` ↔ `Player` only, `Projectile` doesn't collide with itself, etc. The SDK ships sane defaults — only flip cells if you have a reason.
8. Test the Rigidbody by hitting Play in the scratch scene and dropping the object onto a Plane. Confirm: it lands, it doesn't penetrate, it has reasonable mass, it stops in reasonable time.
9. Author **child colliders** for compound shapes — multiple primitives parented under the asset are cheaper and more predictable than a single Mesh Collider.
10. **Apply the asset as a base prefab**: drag from the scene back into `Assets/Content/MyParkName/Prefabs/` to create the prefab. Future props will be variants of these base prefabs.

## Phase 4 — Per-Asset Animation

Animation also belongs at the per-asset stage — animate the asset in isolation, then drop the finished animated prefab into a level.

1. For **humanoid characters**: in the `.fbx` → **Rig tab → Animation Type = Humanoid → Configure...** to verify bone mapping. Save the **Avatar** sub-asset.
2. **Window → Animation → Animator** to open the Animator graph. Right-click `Assets/Content/MyParkName/Prefabs/` → **Create → Animator Controller** named e.g. `Hero.controller`. Drag onto the prefab's `Animator` component.
3. In the **Animator** window, drag clips from the imported model into the graph — they become **States**. Right-click → **Set as Layer Default State**.
4. Add **Parameters** (top-left): `Float Speed`, `Bool IsGrounded`, `Trigger Attack`, etc. Lua will set these via `self:GetComponent(typeof(CS.UnityEngine.Animator)):SetFloat("Speed", v)`.
5. Right-click a state → **Make Transition** → click another state. Adjust **Has Exit Time**, **Transition Duration**, **Conditions** (e.g. `Speed > 0.1`).
6. For **blend trees** (smooth movement-cycle blends on a character): right-click → **Create State → From New Blend Tree** → set **Blend Type = 1D** or **2D Freeform Cartesian**, drag motion clips and set thresholds.
7. **Animation Rigging** for IK: **Add Component → Rig Builder** on the root → child Empty named `IK_Rig` → **Add Component → Rig** → child Empty per IK constraint → **Two Bone IK Constraint** (arms/legs), **Multi-Aim Constraint** (head look), **Chain IK Constraint** (tails/spines). Drag bone references and target Transforms. Bake the rig.
8. For **simple procedural motion** (a spinning gear, a bobbing platform, a pulsing light) just drive it from Lua — don't reach for Animator. A two-line `update()` in Lua replaces an entire Animator graph for these cases.
9. For **shape-key / blendshape** facial animation: confirm the `.fbx` exports blendshapes; on the `Skinned Mesh Renderer`, the `BlendShapes` array becomes drivable from Animator or directly from Lua.
10. **Animation Events** on a clip — **Animation** window → add an event at frame N → name a method (e.g. `Footstep`) → implement that method as a function in the Lua script attached to the same GameObject. Animation Events resolve through the GameObject's `SendMessage`, which reaches `LuaBehaviour` if a matching Lua function name exists.

## Phase 5 — Lua Scripting (Primary Gameplay Layer)

Every gameplay behaviour goes here. **Do not write C#.** The SDK's 108 C# files are the contract — they are not yours to extend, override, or subclass. If a behaviour seems impossible in Lua, the answer is almost always (a) wire it through SDK components you already have, or (b) escalate to the core team.

1. Create the script: right-click `Assets/Content/MyParkName/Scripts/` → **Create → Text** → name with the suffix `.lua.txt` (Unity treats `.lua.txt` as a `TextAsset`). Example: `coin.lua.txt`.
2. **Add Component → LuaBehaviour** to the prefab. Drag `coin.lua.txt` into the `luaScript` slot.
3. **Define lifecycle functions** in the Lua file. The SDK auto-binds these names:

   - `function awake() end` — runs on `MonoBehaviour.Awake`, before `start`.
   - `function start() end` — runs on `MonoBehaviour.Start`.
   - `function update() end` — every frame.
   - `function ondestroy() end` — runs on `MonoBehaviour.OnDestroy`.
   - `function onenable() end` / `function ondisable() end`.
   - `function oncollisionenter(c) end` — `c` is a `UnityEngine.Collision`. **This is the broad-collision interaction pattern.**
   - `function ontriggerenter(c) end` — `c` is a `UnityEngine.Collider`. **This is the space-trigger interaction pattern.**
   - `function onnet(payload) end` — runs when a `NetId.OnNetEvent` arrives.
4. **Access the host GameObject**: `self` is the `LuaBehaviour` instance. Use `self.transform`, `self.gameObject`, `self:GetComponent(typeof(CS.UnityEngine.Rigidbody))`.
5. **Call into Unity** through the `CS` namespace: `CS.UnityEngine.Vector3.up`, `CS.UnityEngine.Time.deltaTime`, `CS.UnityEngine.Mathf.Sin(t)`, `CS.UnityEngine.Object.Instantiate(prefab)`, `CS.UnityEngine.Object.Destroy(go)`.
6. **Inject editor variables** into Lua — on the `LuaBehaviour` Inspector, expand the typed arrays:

   - `Injections` — `GameObject` references, accessed by `name` in Lua.
   - `Float Injections`, `Int Injections`, `String Injections`, `Bool Injections` — primitive values.
   - `Audio Clip Injections` — `AudioClip` references.
   - `Script Injections` — references to **other LuaBehaviours**; what arrives in Lua is the *other script's scope table*, so you can call its functions / read its variables directly: `targetScript.do_something()` or `targetScript.score`.

   In Lua, just use the injection's `name` as a global: if the inspector has an injection named `coinSparkle`, write `coinSparkle:Play()`.
7. Inspector edits **re-inject every frame** — change a slider during Play and the new value is live in Lua next frame. Do not rely on injection identity for once-only setup.
8. **Identify what hit you** by tag, the SDK's intended dispatch surface:

   ```lua
   function ontriggerenter(other)
       if other.tag == "Hand" then ... end
       if other.tag == "Coin" then ... end
       if other.gameObject.layer == CS.UnityEngine.LayerMask.NameToLayer("Player") then ... end
   end
   ```
9. **Drive other scripts** through `Script Injections` — the canonical pattern for cross-prop communication:

   ```lua
   -- inject scoreManager (a LuaBehaviour on the Player) into this prop
   function ontriggerenter(other)
       if other.tag == "Hand" then
           scoreManager.add(1)
       end
   end
   ```
10. **Multiplayer from Lua**: if the GameObject has a `NetId`, the SDK injects `net_send(eventType, payloadJson)` into the script's scope. Send: `net_send("color", '{"r":1,"g":0,"b":0}')`. Receive in `onnet(payload)` — parse with the SDK-provided `json_parse(str)` returning a Lua table.
11. **Coroutines**: `coroutine.create(fn)`, `coroutine.resume(co)`. For Unity-style "wait for seconds", track `CS.UnityEngine.Time.time` in `update()` instead — simpler.
12. **Garbage collection**: `LuaBehaviour` calls `luaEnv:Tick()` once per second automatically. Don't allocate per-frame Lua tables in tight loops; cache them.
13. **Sample structure** for a coin:

    ```lua
    -- coin.lua.txt
    local rotateSpeed = 90  -- degrees / sec

    function start()
        self.transform.position = self.transform.position + CS.UnityEngine.Vector3(0, 0.5, 0)
    end

    function update()
        self.transform:Rotate(CS.UnityEngine.Vector3.up * rotateSpeed * CS.UnityEngine.Time.deltaTime)
    end

    function ontriggerenter(other)
        if other.tag == "Hand" or other.tag == "Player" then
            sparkle:Play()              -- injected ParticleSystem
            scoreManager.add(1)         -- injected ScriptInjection
            CS.UnityEngine.Object.Destroy(self.gameObject)
        end
    end
    ```
14. **Sample structure** for a hammer (item-based interaction):

    ```lua
    -- hammer.lua.txt
    function oncollisionenter(c)
        if c.relativeVelocity.magnitude < 1.5 then return end
        if c.collider.tag == "Peg" then
            local pegScript = c.collider:GetComponent(typeof(CS.LuaBehaviour))
            if pegScript ~= nil then pegScript.ScriptScope.hit() end
            slamSfx:Play()
        end
    end
    ```
15. **Sample structure** for a danger zone (space trigger):

    ```lua
    -- lava_zone.lua.txt
    function ontriggerenter(other)
        if other.tag == "Player" then
            playerHealth.injure(10)
        end
    end
    ```

## Phase 6 — UI (Diegetic Only)

There are **no buttons, no menus, no rays, no pokes, no sliders**. UI in DreamPark is either text/imagery glued to physical props, or it lives on the `DreamBand` wrist surface.

1. The **DreamBand prefab** is the primary UI surface — a wrist band that reflects gameplay state via its state machine (`START`, `STANDBY`, `PLAY`, `PAUSE`, `END`, `COLLECT`, `INJURE`, `RESTART`, `ACHIEVEMENT`, `WIN`, `DESTROY`). Put a `LuaBehaviour` on it and drive its visuals (text, color, particle bursts) from Lua reacting to game events.
2. For **diegetic in-world readouts** (a scoreboard on a wall, a number floating over a peg): **GameObject → UI → Canvas** → set **Render Mode = World Space**, **Event Camera = the OVR Camera Rig's CenterEyeAnchor**, and scale to `0.001` so 1 px ≈ 1 mm. Place it as a child of the prop or attraction.
3. Use **TextMeshPro** (`com.unity.ugui`) for any text. Drop a `TMP_Text`. Drive `text` from Lua: `scoreText.text = tostring(score)`.
4. **No interactive UI elements**. If the player needs to "press" something, it must be a physical prop they touch / hit / step on — i.e. a space trigger or a broad collision, never a UI Button component.
5. Size every readable surface so text is legible from ~1 m away (≥ 30 px font, high contrast against passthrough).
6. Set the canvas `Layer = UI`.

## Phase 7 — Audio Integration

1. Drop **AudioSource** emitters as children of props that emit sound.
2. **Add Component → RealisticRolloff** on each AudioSource — the SDK helper that authors a logarithmic distance curve appropriate for room-scale MR.
3. Set **Spatial Blend = 1.0** (full 3D), **Doppler Level = 0** for static sources, **Volume Rolloff = Custom Rolloff**, **Min Distance = 0.5 m**, **Max Distance = 10 m**.
4. Trigger SFX from Lua. Inject the `AudioClip` and the `AudioSource` (or get the source via `GetComponent`) and call `PlayOneShot`:

   ```lua
   function ontriggerenter(other)
       if other.tag == "Coin" then
           self:GetComponent(typeof(CS.UnityEngine.AudioSource)):PlayOneShot(coinClip)
       end
   end
   ```
5. **Use `CollideAudio.cs`** (`Assets/DreamPark/Scripts/Core/CollideAudio.cs`) on physics props that should self-play impact sounds based on collision velocity — it's a stock SDK component, no scripting required. Drop it on, assign clips, done.
6. **Music** is handled automatically by the `MusicArea` component on the AttractionTemplate — music ducks/swaps between attractions based on player position. Just assign `musicTrack` and `volume` (this happens in Phase 9 when you build the attraction).

## Phase 8 — Multiplayer (Optional)

1. Confirm a `DreamBoxClient` GameObject exists in the scene (drop `Assets/DreamPark/Samples/MultiplayerTest.unity` into your scene as a reference, or copy the `DreamBoxClient` GameObject into yours).
2. Run a local relay during dev: **DreamPark → Multiplayer → Start Local Server**. Confirm the console shows `:7777` and the web panel at `http://127.0.0.1:7780`.
3. **Add Component → NetId** on every prop that should be synchronized. The `Id` is auto-derived from the hierarchy sibling-index path — so the prop's path must be identical on every client (don't reorder siblings between builds).
4. In Lua, define `function onnet(payload) end` — auto-wired when both `NetId` and `LuaBehaviour` are on the same GameObject.
5. Send via the auto-injected `net_send(eventType, payloadJson)` global in your Lua scope:

   ```lua
   net_send("color", '{"r":1,"g":0,"b":0}')

   function onnet(payload)
       local t = json_parse(payload)
       self:GetComponent(typeof(CS.UnityEngine.Renderer)).material.color = CS.UnityEngine.Color(t.r, t.g, t.b, 1)
   end
   ```
6. Test with two Unity Editor instances on the same machine pointing at `127.0.0.1:7777`; the relay fans messages to all peers except the sender.

## Phase 9 — Compose Attractions (Combine Ready-Made Props into Levels)

By this point every prop is a finished prefab in `Assets/Content/MyParkName/Prefabs/` with: physics ✓, collider ✓, layer ✓, tag ✓, animation ✓, Lua script ✓, audio ✓, optional NetId ✓. Now you arrange them into the play space.

1. Right-click `Assets/Content/MyParkName/1. Scenes/` → **Create → Scene** named `MyAttraction.unity`. Or duplicate `Template.unity` to keep the Meta XR rig.
2. Open the new scene. Confirm the **[BuildingBlock] Camera Rig** is at world origin and the **TrackingSpace** child is at `(0, 0, 0)` rotation.
3. **GameObject → Create Empty** named `MyAttraction` at world origin.
4. **Add Component → AttractionTemplate**. The `[RequireComponent]` will auto-add `GameArea` and `MusicArea`. Set: `gameId = MyParkName`, `size = Medium` (or whatever Phase 0 defined), `defaultAnchorPosition = (0, -3.2)` (where the player portal-spawns relative to the floor center, in meters), `generateFloor = true`, `floorMaterial = Assets/DreamPark/Materials/Occlusion.mat`.
5. Drag `Assets/Content/MyParkName/2. Features/1. Player/Player.prefab` into the Hierarchy as a child of the scene root (NOT a child of the Attraction). Set its `PlayerRig.gameId = MyParkName` to match.
6. Drag `Assets/Content/MyParkName/2. Features/2. DreamBand/DreamBand.prefab` into the Hierarchy. Set `DreamBand.gameId = MyParkName`.
7. On the `MyAttraction` GameObject's `MusicArea` component, assign `musicTrack` to the AudioClip from Phase 2 step 10. Set `volume = 0.6`, `priority = 0`.
8. **Drag prop prefabs** from `Assets/Content/MyParkName/Prefabs/` into the Hierarchy as children of the Attraction. They inherit calibration from the Attraction.
9. For each prop instance, confirm its **PropTemplate** component (auto-added when you authored it) is configured: `category` is correct, `affectsGapFiller = true` for floor-occupying props, `useColliderBounds = true` if your collider snugly matches the visual.
10. **Cut floor holes** for pits / passages: child Empty → **Add Component → FloorCutout** → in the Inspector add Vector2 `points` describing the polygon (in level-local space). Gizmos preview the cut.
11. Add a **GapFiller** if you have non-axis-aligned floor geometry: `MyAttraction` → child Empty → **Add Component → GapFiller**. It auto-subscribes to `LevelTemplate.OnAnyLevelTemplateChanged`.
12. Verify the purple Gizmo wireframe of the AttractionTemplate matches your physical room. The orange "human reference" gizmo at `defaultAnchorPosition` shows where the player will spawn.
13. Save the scene. The attraction prefab in `Assets/Content/MyParkName/2. Features/3. Level/Level.prefab` is the canonical authoring artifact — when ready, drag your Attraction GameObject onto it to update.

## Phase 10 — NavMesh & AI (For Attractions With Enemies)

1. Every `LevelTemplate` auto-builds a **NavMeshSurface** at runtime via `BuildNavSurfaceAndAnchors()` after generating the floor. You don't manage this.
2. Place a `NavMeshAgent` on enemy prefabs and set `agentTypeID` matching the surface (the SDK auto-syncs the agent type if the first agent's ID is found).
3. Drive the agent from Lua:

   ```lua
   -- enemy.lua.txt
   local agent = nil
   local player = nil

   function start()
       agent = self:GetComponent(typeof(CS.UnityEngine.AI.NavMeshAgent))
       player = CS.UnityEngine.Camera.main.transform
   end

   function update()
       if player ~= nil then agent:SetDestination(player.position) end
   end
   ```
4. **NavMesh modifiers**: drop `NavMeshModifier` on obstacle props (e.g. `Hazard` props) to carve them out of the walkable surface; `NavMeshModifierVolume` for explicit zones.
5. For **non-collider blocking** (invisible walls): primitive Box Collider with **Is Trigger = false** on layer `Level`.

## Phase 11 — Calibration & Build/Play Mode Toggling

1. The SDK runs in two modes: **Build Mode** (player is laying out the room) and **Play Mode** (gameplay). `LevelTemplate.isBuildMode` returns `true` outside of Play Mode in the editor, and mirrors the iOS `appState == "BUILD"` at runtime.
2. In **Build Mode** the runtime floor swaps from layer `Level` to layer `Water` so it doesn't occlude the AR mesh during placement.
3. **Test calibration in editor**: select the Attraction → **Inspector → "Test Real World Calibration"** button. The SDK loads `Assets/DreamPark/Models/Park.fbx` as a stand-in for the AR mesh and runs the calibration loop.
4. **CalibrateLevel** (auto-attached to the runtime floor) raycasts from each `FloorAnchor` to the AR mesh, deforms the floor mesh to match. **CalibrateProp** (auto-attached to props at `Start`) snaps the prop's Y to the surface beneath it.
5. After calibration, `LevelTemplate.RegenerateFloor()` re-cuts the floor to respect updated `FloorCutout` polygons.

## Phase 12 — Optimization

1. Quest 3S targets **72 / 90 / 120 Hz**; aim for 90 Hz with a frame budget of ~11 ms. The on-screen `OptimizedAF.FPSDisplay` shows live FPS.
2. **Edit → Project Settings → Quality**: confirm the Android quality level uses the URP asset shipped with the SDK.
3. **Renderer settings**: Forward+, MSAA 4x, post-processing minimal (no SSAO, no depth-of-field on Quest).
4. Use **GPU Instancing** on materials (Material Inspector → Enable GPU Instancing).
5. **Static batching**: mark stationary geometry **Static → Batching Static** in the Inspector.
6. **Texture max size 1024** for props, **512** for distant decoration. **ASTC 6x6** for albedo, **ASTC 4x4** for normals.
7. **Mesh compression = High** for non-deforming meshes.
8. The SDK's `OptimizedAF` system auto-disables `Animator`, `Particle System`, and `Audio Source` on objects far from `Camera.main`. Mark anything that must always run with **Add Component → OptimizedAFIgnore**.
9. **Profiling**: **Window → Analysis → Profiler** with the headset connected. Watch CPU main thread, GPU, and `gfx.WaitForPresent`. **Window → Analysis → Frame Debugger** for draw-call breakdown.
10. **Lua perf**: avoid per-frame table allocations, cache `CS.UnityEngine.Time.deltaTime` once per `update()`, prefer `Vector3.zero` constants from CS.
11. Run `DreamPark → Troubleshooting → Force Update All Content` after big asset reorganizations.

## Phase 13 — Testing

1. **Editor Play Mode** with Quest Link / Air Link — fastest iteration. Hand tracking, passthrough, and OpenXR all work.
2. **Build & Run to device**: **File → Build Profiles → Android → Build And Run** with the Quest 3S in **Developer Mode** plugged via USB-C (`adb devices` shows `Quest3S unauthorized` until you accept the prompt in-headset).
3. **adb logcat**: `adb logcat -s Unity:* DreamPark:* OVR:*` to watch logs from the headset. The SDK uses `Debug.Log("[GameArea] ...")` style prefixes for filtering.
4. **Test the multiplayer relay** with a second Editor instance and a second device (`DreamPark → Multiplayer → Start Local Server` then `Open Control Panel`).
5. **Sanity matrix** — verify each item:

   - Player walks into the Attraction → `GameArea` enters → `PlayerRig` shows → `DreamBand` shows.
   - Player exits the Attraction → both hide.
   - Each prop's `ontriggerenter` / `oncollisionenter` fires when expected.
   - Item-based interactions: grabbed prop's collisions register against intended targets.
   - Floor calibration matches the real room within ±5 cm.
   - FPS holds at target Hz throughout a 5-minute play session.
   - Audio music swaps cleanly when entering / leaving attractions.
6. Run `DreamPark → Troubleshooting → Remove Broken Addressables` to clean stale Addressable references before publishing.
7. **Edit → Project Settings → Player → Android → Resolution and Presentation**: confirm **Default Orientation = Landscape Left**, **Optimized Frame Pacing = on**.

## Phase 14 — Publish (Addressables Are Automatic)

You do not manage Addressables by hand. The SDK auto-bundles anything that is a `PropTemplate` or `AttractionTemplate` prefab living under `Assets/Content/MyParkName/`. Your only job is keeping things in the right folder with the right component.

1. Confirm every prefab you want shipped is under `Assets/Content/MyParkName/` and has a `PropTemplate` (for props) or `AttractionTemplate` (for levels) on its root. Anything else is build-only or scratch.
2. Confirm every prefab has a non-empty `gameId` matching your park name. The SDK's auto-inclusion logic uses `gameId` to group bundles.
3. **DreamPark → Content Uploader** — opens the publish window:

   - Pick build targets: **Android** (required for Quest), and optionally **iOS / StandaloneOSX / StandaloneWindows** for cross-platform companion apps.
   - **Clean Before Each Target** = on for the first publish.
   - Click **Upload Content (Build & Push)** — the SDK builds Addressables for every selected target and pushes to DreamPark servers.
   - Click **Upload Content (Push Only)** if bundles are already built.
4. Verify on a real Quest 3S that the published park is reachable through the DreamPark client app and the entire flow plays end-to-end.
5. **Tag a release**: `git tag v0.1.0 && git push --tags`.

## Phase 15 — Iteration & Maintenance

1. After any SDK update, run `DreamPark → Sync Tags & Layers from Core` and `DreamPark → Check for SDK Updates`.
2. Periodically run `DreamPark → Troubleshooting → Find Unused Scripts` and `Restore Prefab Saveability`.
3. Keep `Assets/DreamPark/` untouched — those 108 files must match `dreampark-core` byte-for-byte. Everything you author lives in `Assets/Content/MyParkName/`.
4. Keep prop prefab paths stable — `NetId` IDs depend on hierarchy paths.
5. Re-run `DreamPark → Troubleshooting → Regenerate Level Previews` after visual changes to keep in-app browse art current.
6. Submit feedback via **Open manual** / **Join our Discord** menu items.

---

## Quick Cross-Reference

| You want to... | Do this |
| --- | --- |
| React when a hand touches a prop | Trigger collider + `LuaBehaviour` with `ontriggerenter` checking `other.tag == "Hand"` |
| React when one prop hits another | Solid collider + Rigidbody + `LuaBehaviour` with `oncollisionenter` checking `c.collider.tag` |
| Detect player walking into a zone | Trigger collider on layer `Triggers` + `LuaBehaviour` with `ontriggerenter` checking `other.tag == "Player"` |
| Activate a whole attraction on player entry | `AttractionTemplate` on it (auto-adds `GameArea`); set `gameId`. The PlayerRig matching `gameId` auto-shows |
| Score points | A `LuaBehaviour` on the Player called `scoreManager`; inject it into props as a `Script Injection`; props call `scoreManager.add(1)` |
| Spin a gear forever | Two-line Lua `update()` calling `self.transform:Rotate(...)` |
| Sync object across players | `NetId` on the prop + `function onnet(payload)` and `net_send("type", json)` in the Lua |
| Play looping music for a room | `MusicArea` on the Attraction, assign `musicTrack` |
| Cut a hole in the floor | `FloorCutout` child of the Attraction with polygon `points` |
| Animate a character | `Animator` + `Animator Controller` + Humanoid Avatar; drive params from Lua via `SetFloat` / `SetTrigger` |
| Animate a simple prop motion | Don't use Animator — write 2 lines of Lua in `update()` |
| Add a wrist-UI readout | Drive DreamBand visuals from a Lua script reacting to game state |
| Define the play space size | `AttractionTemplate.size` enum or `Custom` Vector2 in feet |
| Spawn the player | `defaultAnchorPosition` on the AttractionTemplate |
| Publish the park | `DreamPark → Content Uploader` — Addressables build automatically |
