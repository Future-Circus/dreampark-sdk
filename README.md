# DreamPark SDK

**Build mixed-reality attractions for real-world theme parks. Upload from Unity. Earn on every play.**

[**Developer program →**](https://dreampark.app/developer) · [**Documentation →**](https://dreampark.app/docs) · [**Revenue terms →**](https://dreampark.app/developer-terms) · [**Create an account →**](https://dreampark.app/signup?developer=true)

---

DreamPark is a mixed-reality theme park platform. It lets real venues run fully virtual attractions — hours of entertainment for visitors carrying nothing but a Quest 3 headset. No wearables, no controllers, no per-attraction hardware to install.

The part no one else does: **attractions from different developers run side by side in the same venue.** An operator sets up and manages an entire lineup in minutes from a simple mobile app, mixing your attraction in with everyone else's. Your build downloads to the venue, runs on the headset, and earns you a share of every play.

This repo is the SDK: a complete, ready-to-open **Unity 6** project. Clone it, name your game, build an attraction, and push it live from the editor. There's no store submission and no review queue — uploads are playable on real hardware the same day.

## Why build here

- **You get 50% of every play.** Operators pay an entry fee for each session at their park. Half of that goes to developers, and the split follows the guests: the more of a session they spend inside your attraction, the more of the pool you take. If they only play yours, the whole developer share is yours. [Full terms in plain English →](https://dreampark.app/developer-terms)
- **You're not competing for a venue, you're joining one.** Attractions from different developers coexist in the same space, so an operator adding your work doesn't have to drop anyone else's.
- **One-click publishing.** The in-editor Content Uploader bundles your build and pushes it to DreamPark. No submission process, no waiting room.
- **Live immediately.** New uploads appear in the DreamPark mobile app right away under **Experimental Mode**, so you can playtest on a headset minutes after building.
- **Bring the Unity content you already have.** A prefab library, an old jam project, a half-finished game — drop it under an `AttractionTemplate` and convert the gameplay scripts to Lua. Most C# translates line for line.
- **Ship code updates instantly.** Gameplay is written in Lua and pushes over the air. No app release, no rebuild.

A fuller tour of the program lives at **[dreampark.app/developer](https://dreampark.app/developer)**.

## Quick start

**You need:** a **Meta Quest 3S** (or Quest 3) in Developer Mode, a USB-C **data** cable, [Unity Hub](https://unity.com/download), and [Git LFS](https://git-lfs.com).

1. **Install Unity `6000.3.23f1`** via Unity Hub, with **Android Build Support** (including its OpenJDK and Android SDK & NDK sub-modules) and **iOS Build Support** — the uploader builds both on every release and neither can be turned off. Add **Mac** and **Windows Build Support** too; those two editor targets are toggles in the upload window, on by default, and required for an official release.
2. **Install Git LFS, then clone this repo.** The SDK's art, audio, models and native plugins live in [Git LFS](https://git-lfs.com). Without it the clone still *appears* to work, but every binary arrives as a small text placeholder and the project opens broken.
   ```bash
   git lfs install   # once per machine — before cloning
   git clone https://github.com/Future-Circus/dreampark-sdk.git MyGame
   ```
   Already cloned without it? Open the project and run `DreamPark → Troubleshooting → Check Git LFS` — the SDK finds the placeholders and downloads the real files for you.
3. **Open it in Unity** — Unity Hub → Add → pick the folder. Let packages resolve (a few minutes), then give your game an ID in the setup popup. Use **letters and digits only, starting with a letter**, 2–64 characters (`CoinCollector`); dashes, spaces and underscores break uploads. That renames `Assets/Content/YOUR_GAME_HERE/` and rewrites the `gameId` references inside it — **the folder name is your content ID**. Dismissed the popup? `DreamPark → Content Uploader` → **Set Content ID** reopens it.
4. **Sign in:** `DreamPark → Sign In`. Sign-in is passwordless — enter your email, then the 6-digit code we send you. There's no separate sign-up step: the first code you verify creates the account.
5. **Look at the starting scene:** `Assets/[StartHere].unity` opens by itself the first time the project comes up. It has the Meta camera rig, hand tracking, passthrough and occlusion pre-wired, plus your `Player.prefab` and `Attraction.prefab` — build here. Point it somewhere else with `DreamPark → Startup Scene...`.
6. **Verify your headset:** put it in Developer Mode, connect over Quest Link (USB-C data cable) or Air Link, accept the prompts in the headset, press **Play**, and confirm passthrough and hand tracking work.
7. **Upload:** `DreamPark → Content Uploader`. Fill in the title and description there, then hit **Compile & Upload**. That opens the launch window for release notes and build targets; hit **Start · All** to ship. Your first release always uploads everything; from the second on, the **Upload Scope** picker offers **Patch** (changed files only) and **Code only** (just the Lua bundle) for much smaller uploads.
8. **Play it:** open the DreamPark iOS app (private TestFlight beta — email **aidan@dreampark.app** for an invite) and toggle **Experimental Mode** on in your park settings.

If you use ClaudeBots, this repository includes a human-readable `RELEASE.md`. Attach the project as a bot or group workspace, assign **Plan & Verify Releases**, and ask the bot to read the release file and add its workflow as drafts. The bot interprets the prose against ClaudeBots' typed target catalog; ClaudeBots verifies the exact file digest and rejects secrets, unsafe paths, and unknown settings before creating a disabled **DreamPark Content Publish** target plus a draft workflow. Select the project-specific content ID, finish email-code sign-in in Unity, verify the target, and activate the workflow when it is correct. The file cannot publish by itself. `RELEASE.json` remains available only for advanced compatibility.

From there the loop is: edit → Content Uploader → Compile & Upload → Start → reopen on the Quest.

**Want a worked example?** `Assets/Content/Sample/` is a complete project that ships with the SDK — two attractions, five props, a scene, and the Lua behind them. Browse and edit it freely, but build your own game elsewhere: `Sample` is a reserved name and can't be published, so copy what you need into your own folder.

## The three primitives

Everything you ship is built from three pieces.

| | What it is |
|---|---|
| **`Player.prefab`** | Your game's global systems. Persists across all your attractions — score managers, audio, park-wide state. One per game. |
| **`AttractionTemplate`** | A self-contained experience (`A_MyAttraction.prefab`) — an arcade game, a boss fight, a challenge course. This is the unit players play, operators install, and revenue attributes to. Auto-adds a `GameArea` (presence detection, **and the playtime your revenue share is measured on**) and a `MusicArea`. |
| **`PropTemplate`** | An interactive object (`P_MyProp.prefab`) — a coin, a hammer, an enemy, a block. Lives inside attractions, and operators can also place props individually when decorating. |

Parks contain Attractions. Attractions contain Props. The Player runs your global systems above all of it.

All of your work lives in `Assets/Content/<your game ID>/`. Never hand-edit Addressables — the SDK's `ContentProcessor` stamps addresses, labels and `gameId` fields for you. Preview tile art is auto-generated into `Previews/`.

Each of your subfolders under `Assets/Content/` is an independent package, so several games can share one project; the Content Uploader picks which to publish, and each versions and earns separately. Two names are reserved and never publishable: `Sample` (the bundled example) and `YOUR_GAME_HERE` (the template folder before you rename it).

## Write gameplay in Lua

Gameplay logic belongs in `.lua.txt` scripts on `LuaBehaviour` components, powered by [XLua](https://github.com/Tencent/xLua). XLua maps Unity's API 1:1 — anything you can do in a `MonoBehaviour` you can do in a `LuaBehaviour` — and **Lua ships over the air**, so fixes reach live venues without an app release.

New **C# scripts** require manual review and only run on device after a future app release. If you're porting existing content, convert the gameplay scripts as you bring them in.

## Platform APIs

Your attraction can read and write the guest's DreamPark profile:

- **[Game Storage](https://dreampark.app/docs#storage)** — per-player save data: high scores, coins, checkpoints, progress flags.
- **[Profile API](https://dreampark.app/docs#profile)** — who the guest is and what they own.
- **[Achievements, badges & items](https://dreampark.app/docs#rewards)** — define them in the developer portal, award them from Lua.
- **[Scores & the Adventure Log](https://dreampark.app/docs#scores)** — write a high score and it turns into a highlighted moment on the guest's timeline and in the park's public feed.
- **[Multiplayer](https://dreampark.app/docs#multiplayer)** — local peer-to-peer over the venue's Wi-Fi. No servers, no matchmaking; headsets find each other and play.

> [!IMPORTANT]
> **Profile and Storage writes require the player to enter your attraction first.**
>
> A guest wandering a venue hasn't opted into every game installed there. Awards and save-data writes are held until the player physically steps into one of your `GameArea`s — then everything queued is sent, in order, and stays open for the rest of their visit. Walking back out doesn't close it again.
>
> This is invisible in normal use, because the natural place to write is inside the attraction the guest is standing in. It shows up if a park-wide script on `Player.prefab` writes on `start()`, or when you're testing a lone prop in the editor with no `GameArea` to walk into (editor sessions open the gate automatically — see `ContentGate.AutoOpenInEditor`).
>
> **Reads are never gated**, so gameplay behaves normally either way.

Guests can also **delete their saved data at any time**, so design every read to survive its key being missing — always pass a default. [More on that here.](https://dreampark.app/docs#storage)

Working samples ship in `Assets/DreamPark/Samples/` — `GameStorage/`, `ProfileAPI/`, and `Multiplayer/`.

### Height-aware ergonomics

The selected guest profile includes physical height for reachable controls, eye-level targets, and other ergonomic placement. The authoring reference is **5 ft 8 in (68 in)**. Lua exposes absolute inches, metres, and a dimensionless factor:

```lua
local inches = dp.profile.getHeightInches() -- 68 when missing/not loaded
local metres = dp.profile.getHeightMeters() -- 1.7272 at the reference height
local factor = dp.profile.getHeightFactor() -- guest inches / 68

-- For a target authored relative to a floor/reference plane:
targetY = floorY + (authoredY - floorY) * factor

-- Subscribe after the active profile is ready. Updates are local to this
-- headset/profile; they are not replicated to other players or netIds.
local unsubscribe = nil
dp.profile.onReady(function()
    targetY = floorY + (authoredY - floorY) * dp.profile.getHeightFactor()
    unsubscribe = dp.profile.onHeightChanged(function(newInches, newFactor)
        targetY = floorY + (authoredY - floorY) * newFactor
    end)
end)

-- Call when the script/object is torn down. Safe to call more than once.
if unsubscribe ~= nil then unsubscribe() end
```

The equivalent C# properties are `ProfileAPI.HeightInches`, `HeightMeters`, and `HeightFactor`; subscribe to `ProfileAPI.OnHeightChanged`, whose arguments are `(heightInches, heightFactor)`. Profiles support 24–96 inches; older or malformed snapshots safely use 68 inches and a factor of `1.0`.

Initial profile hydration does not emit a change event: use `onReady`/`OnReady` to apply the baseline, then subscribe for live changes. `dp.profile.onHeightChanged` returns an idempotent unsubscribe function and its subscription is automatically cleared when that headset's identity is cleared. Height is a local ergonomic presentation value, so every user can render the same networked object (`netId`) at their own reachable height without moving it for anyone else. See `Assets/DreamPark/Samples/ProfileAPI/height.lua.txt` for a drop-in example.

## Uploading

You don't need a finished park to publish. Every upload bundles whatever attractions and props exist in your content folder, and the catalog updates automatically — there's no manual registration step.

- **One at a time** — build a single attraction, upload it, see it live the same day, add more in later versions. Recommended for a first release.
- **A park's worth** — author a full set and publish them together.

## Troubleshooting

<details>
<summary><strong>Setup and build</strong></summary>

- **"Editor version not found" in Unity Hub** — `6000.3.23f1` isn't installed. Install that exact editor; the project is pinned to it so local, CLI, and release builds use the same toolchain.
- **Pink materials, silent audio, missing meshes, or a native plugin that won't load** — the repo was cloned without Git LFS, so those assets are text placeholders rather than real files. Run `DreamPark → Troubleshooting → Check Git LFS` to download them, or from a terminal in the project folder: `git lfs install && git lfs pull`. The SDK also checks for this automatically when the project opens.
- **A dialog asks to send "XLua Version / Unity Version / Device Identifier"** — that's XLua's own analytics ping, and it's removed from this version of the SDK. If you're on an older clone, either button is safe; **Deny** just skips the ping and nothing in the SDK depends on it.
- **Console errors on first open** — let Unity finish downloading packages. Still broken? Close Unity, delete the project's `Library` folder, reopen.
- **Quest doesn't appear when you press Play** — check Developer Mode is on, that the USB-C cable carries data (not charge-only), and that you accepted the USB Debugging prompt inside the headset.
- **The sign-in code doesn't arrive** — check spam, wait out the 30-second cooldown, then **Resend code**. The server allows three sends per address per ten minutes, so don't burn them faster than the email can land. There is no password to reset.
</details>

<details>
<summary><strong>Publishing and content</strong></summary>

- **Upload fails partway** — usually a network blip. Hit **Try Reupload** in the Content Uploader; it re-sends what's already built without recompiling, and if some bundles failed last run it offers to re-send just those. If it persists, check the Unity Console for the specific error.
- **Attraction doesn't appear in the app** — make sure **Experimental Mode** is on in your park settings, and that you're signed into the same account as the Content Uploader.
- **Missing from the Attractions browser** — the prefab root needs an `AttractionTemplate` (or `PropTemplate`); that component is what stamps the address the catalog classifies on. Naming attractions `A_` and props `P_` is still the house convention but no longer affects discovery — with one exception: **never name a new attraction `L_*`**, which marks a pre-attraction legacy level and hides it from the browser.
- **Loads on mobile but doesn't behave** — new C# scripts need manual approval and a future app release before they run on device. Use Lua for fast iteration.
</details>

<details>
<summary><strong>Profile, storage and awards</strong></summary>

- **Awards or saves seem to do nothing** — check the Unity Console. Every rejected write logs the reason, and held writes log that they're waiting for the player to enter an attraction.
- **`429` responses** — profile writes are rate limited per guest, well above what a real attraction does. Award on game events (a pickup, a run ending), not in `update()`.
- **Nothing happens in the editor** — run `DreamPark → Sign In`, then `DreamPark → Profile → Bind to Logged-In User`. The preview session expires after a while; re-bind when writes start failing.
</details>

## Learn more

| | |
|---|---|
| [**dreampark.app/developer**](https://dreampark.app/developer) | The developer program — how earning works, what a venue is like |
| [**dreampark.app/docs**](https://dreampark.app/docs) | Full documentation — primitives, Lua scripting, storage, profile, rewards, multiplayer |
| [**dreampark.app/developer-terms**](https://dreampark.app/developer-terms) | The 50% playtime split, in plain English |
| `PIPELINE.md` | End-to-end build pipeline checklist, in this repo |

Questions: **community@dreampark.app**

---

© Dream Park Immersive, Inc. See [`LICENSE`](LICENSE) and [`NOTICE.md`](NOTICE.md).
