# DreamPark SDK — Getting Started

This is the setup guide. Follow it top to bottom and at the end you'll have the project open in Unity, building to a Quest 3S, ready for you to start adding content. For how to actually author a park, open **DreamPark → Open Manual** from the top menu inside Unity.

---

## What you need before you start

- A **Meta Quest 3S** in **Developer Mode**, paired to your Meta account. (Developer Mode is enabled in the Meta Horizon mobile app under Devices → your headset → Developer Mode.)
- A **USB-C cable** that supports data (the one in the Quest box works) — or Air-Link set up, but USB is simpler for first-time setup.
- A computer with **~30 GB free disk space**.
- **GitHub Desktop** (or any Git client you prefer). Download from [desktop.github.com](https://desktop.github.com) — it's free.
- **Unity Hub**. Download from [unity.com/download](https://unity.com/download).

---

## 1. Install Unity

1. Open **Unity Hub**.
2. Go to the **Installs** tab → **Install Editor**.
3. Pick version **`6000.0.39f1`**. If you don't see it in the list, click **Archive** / **Install Editor → Official Releases** and find it there. (Exact version matters — newer or older Unity 6 versions will not work.)
4. On the **Add modules** screen, check **Android Build Support** and make sure both sub-modules under it are checked: **OpenJDK** and **Android SDK & NDK Tools**.
5. Click **Install** and wait. This takes a while (multiple GB).

---

## 2. Get the SDK onto your computer

1. Open **GitHub Desktop**.
2. **File → Clone Repository → URL** tab.
3. Paste the DreamPark SDK URL into the **Repository URL** field.
4. Pick a folder on your computer to put it in (somewhere you can find it later — Documents is fine).
5. Click **Clone**.

When it finishes you'll have a folder containing the whole SDK on your computer.

---

## 3. Open the project in Unity

1. In **Unity Hub**, click **Add → Add project from disk**.
2. Pick the SDK folder you cloned in step 2.
3. Click it in the project list to open it. **Make sure the Editor Version column shows `6000.0.39f1`.**
4. First open will take a long time — Unity downloads packages (Meta XR SDK, Oculus, Addressables, etc.). Let it finish completely before doing anything. You'll know it's done when the bottom-right progress spinner stops.

---

## 4. Switch the build target to Android

1. **File → Build Profiles**.
2. Select **Android** in the platform list.
3. Set **Texture Compression** to **ASTC**.
4. Click **Switch Platform**. This takes a few minutes — Unity is recompressing assets.

---

## 5. Confirm Player settings

**Edit → Project Settings → Player → Android tab → Other Settings.** Confirm these match:

- **Minimum API Level:** 32
- **Target API Level:** 34
- **Scripting Backend:** IL2CPP
- **Target Architectures:** ARM64 (uncheck ARMv7 if it's on)

---

## 6. Confirm XR settings

**Edit → Project Settings → XR Plug-in Management → Android tab.**

- Enable **OpenXR**.
- Enable **Oculus**.
- Under **OpenXR → Feature Groups**, enable **Meta Quest Support**, **Hand Tracking Subsystem**, and **Meta XR Foundation**.

---

## 7. Sync tags & layers from the SDK

Top menu: **DreamPark → Sync Tags & Layers from Core**.

This lines up the layer indices the SDK depends on. Skip it and physics will not behave correctly.

---

## 8. Check for SDK updates

Top menu: **DreamPark → Check for SDK Updates**. Accept anything it offers.

---

## 9. Open the editing scene

In the **Project** window inside Unity, navigate to `Assets/Content/YOUR_GAME_HERE/1. Scenes/` and double-click **`Template.unity`** to open it. This scene has an example **Attraction** and **Prop**.

(You'll rename `YOUR_GAME_HERE` to your actual park name later — for now leave it as-is.)

---

## 10. Plug in your Quest 3S and press Play

1. Plug your Quest 3S into your computer via USB-C.
2. **Put the headset on** — you'll get an "Allow USB Debugging?" prompt the first time. Hit **Allow** (and check "Always allow from this computer").
3. In Unity, click the **Play** button at the top of the editor.
4. In the headset you should see passthrough (your real room), your hands tracked, and an FPS counter in the corner.

If that all works, **you're done with setup.** You're ready to start adding content.

---

## What now?

- The `Template.unity` scene you just opened has an **Attraction** and a **Prop** already wired up — poke at them in the Inspector to see how the pieces fit together. That's the fastest way to get a feel for the SDK.
- For the full authoring guide (assets, props, attractions, Lua scripts, multiplayer, publishing) — open **DreamPark → Open Manual** from the top menu inside Unity.

---

## If something went wrong

A few things that commonly trip people up on first setup:

- **"Editor version not found" in Unity Hub** — you don't have `6000.0.39f1` installed. Go back to step 1.
- **Packages won't resolve / errors in console on first open** — let Unity finish downloading packages before you touch anything; the resolver runs for several minutes on a fresh clone. If it still fails, close Unity, delete the `Library` folder at the project root, and re-open.
- **Quest doesn't show up when you hit Play** — make sure Developer Mode is on (Meta Horizon mobile app), the USB cable supports data (not just power), and you accepted the USB Debugging prompt inside the headset.
- **Black screen / no passthrough in headset** — confirm step 6 (XR settings) is done. OpenXR and Oculus both need to be enabled.
- **Anything else** — check the manual via **DreamPark → Open Manual**.
