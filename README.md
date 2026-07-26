# DreamPark SDK

**Build mixed-reality attractions for real-world theme parks. Upload from Unity. Earn on every play.**

[**Developer program →**](https://dreampark.app/developer) · [**Documentation →**](https://dreampark.app/docs) · [**Revenue terms →**](https://dreampark.app/developer-terms) · [**Create an account →**](https://dreampark.app/signup?developer=true)

---

DreamPark is a chain of physical mixed-reality venues. Guests arrive, put on a Quest 3 and a DreamBand wristband, and walk into your attraction in a real room — hands tracked, passthrough on, no controllers.

This repo is the SDK: a complete, ready-to-open **Unity 6** project. Clone it, name your game, build an attraction, and push it live from the editor. There's no store submission and no review queue — uploads are playable on real hardware the same day.

## Why build here

- **You get 50% of every play.** Operators pay an entry fee for each session at their park. Half of that goes to developers, and the split follows the guests: the more of a session they spend inside your attraction, the more of the pool you take. If they only play yours, the whole developer share is yours. [Full terms in plain English →](https://dreampark.app/developer-terms)
- **One-click publishing.** The in-editor Content Uploader bundles your build and pushes it to DreamPark. No submission process, no waiting room.
- **Live immediately.** New uploads appear in the DreamPark mobile app right away under **Experimental Mode**, so you can playtest on a headset minutes after building.
- **Bring the Unity content you already have.** A prefab library, an old jam project, a half-finished game — drop it under an `AttractionTemplate` and convert the gameplay scripts to Lua. Most C# translates line for line.
- **Ship code updates instantly.** Gameplay is written in Lua and pushes over the air. No app release, no rebuild.

A fuller tour of the program lives at **[dreampark.app/developer](https://dreampark.app/developer)**.

## Quick start

**You need:** a **Meta Quest 3S** (or Quest 3) in Developer Mode, a USB-C **data** cable, and [Unity Hub](https://unity.com/download).

1. **Install Unity `6000.0.58f2`** via Unity Hub, with **Android Build Support** (including its OpenJDK and Android SDK & NDK sub-modules) and **iOS Build Support**.
2. **Clone this repo:**
   ```bash
   git clone https://github.com/Future-Circus/dreampark-sdk.git MyGame
   ```
3. **Open it in Unity** — Unity Hub → Add → pick the folder. Let packages resolve (a few minutes), then give your game an ID in the popup. That renames the placeholder content folder; **the folder name is your content ID**.
4. **Sign in:** `DreamPark → Sign In`. New here? Hit **Sign Up** in the popup first.
5. **Open the example scene:** `Assets/Content/<your game ID>/1. Scenes/Template.unity` — an example Attraction and Prop, already wired up.
6. **Verify your headset:** plug in over USB-C, accept *Allow USB Debugging?* in the headset, press **Play**, and confirm passthrough and hand tracking work.
7. **Upload:** `DreamPark → Content Uploader` → **Upload Content (Build & Push)**.
8. **Play it:** open the DreamPark iOS app (private TestFlight beta — email **aidan@dreampark.app** for an invite) and toggle **Experimental Mode** on in your park settings.

From there the loop is: edit → Content Uploader → Build & Push → reopen on the Quest.

## The three primitives

Everything you ship is built from three pieces.

| | What it is |
|---|---|
| **`Player.prefab`** | Your game's global systems. Persists across all your attractions — score managers, audio, park-wide state. One per game. |
| **`AttractionTemplate`** | A self-contained experience (`A_MyAttraction.prefab`) — an arcade game, a boss fight, a challenge course. This is the unit players play, operators install, and revenue attributes to. Auto-adds a `GameArea` (presence detection, **and the playtime your revenue share is measured on**) and a `MusicArea`. |
| **`PropTemplate`** | An interactive object (`P_MyProp.prefab`) — a coin, a hammer, an enemy, a block. Lives inside attractions, and operators can also place props individually when decorating. |

Parks contain Attractions. Attractions contain Props. The Player runs your global systems above all of it.

All of your work lives in `Assets/Content/<your game ID>/`. Never hand-edit Addressables — the SDK's `ContentProcessor` stamps addresses, labels and `gameId` fields for you. Preview tile art is auto-generated into `Previews/`.

Each subfolder of `Assets/Content/` is an independent package, so several games can share one project; the Content Uploader picks which to publish, and each versions and earns separately.

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

## Uploading

You don't need a finished park to publish. Every upload bundles whatever attractions and props exist in your content folder, and the catalog updates automatically — there's no manual registration step.

- **One at a time** — build a single attraction, upload it, see it live the same day, add more in later versions. Recommended for a first release.
- **A park's worth** — author a full set and publish them together.

## Troubleshooting

<details>
<summary><strong>Setup and build</strong></summary>

- **"Editor version not found" in Unity Hub** — `6000.0.58f2` isn't installed. Install it.
- **Console errors on first open** — let Unity finish downloading packages. Still broken? Close Unity, delete the project's `Library` folder, reopen.
- **Quest doesn't appear when you press Play** — check Developer Mode is on, that the USB-C cable carries data (not charge-only), and that you accepted the USB Debugging prompt inside the headset.
- **Sign-in fails** — reset your password from the **Sign Up** link in the popup, then reopen `DreamPark → Sign In`.
</details>

<details>
<summary><strong>Publishing and content</strong></summary>

- **Upload fails partway** — usually a network blip; retry. If it persists, check the Unity Console for the specific error.
- **Attraction doesn't appear in the app** — make sure **Experimental Mode** is on in your park settings, and that you're signed into the same account as the Content Uploader.
- **Missing from the Attractions browser** — the prefab root needs an `AttractionTemplate` (or `PropTemplate`); that component is what produces the catalog entry. Avoid the `L_` prefix, which is reserved for legacy levels.
- **Loads on mobile but doesn't behave** — new C# scripts need manual approval and a future app release before they run on device. Use Lua for fast iteration.
</details>

<details>
<summary><strong>Profile, storage and awards</strong></summary>

- **Awards or saves seem to do nothing** — check the Unity Console. Every rejected write logs the reason, and held writes log that they're waiting for the player to enter an attraction.
- **`429` responses** — profile writes are rate limited per guest, well above what a real attraction does. Award on game events (a pickup, a run ending), not in `update()`.
- **Nothing happens in the editor** — run `DreamPark → Sign In`, then `DreamPark → Profile → Bind to Logged-In User`. The preview session lasts about an hour; re-bind if it expires.
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
