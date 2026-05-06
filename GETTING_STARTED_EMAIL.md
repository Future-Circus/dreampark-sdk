# Getting Started Email — DreamPark SDK

A short, email-friendly version of the Getting Started Guide. Paste the section between the dashed lines into your mail client.

---

**Subject:** Welcome to the DreamPark SDK — your first 10 steps

Hi there,

Welcome aboard! You're about to build a room-scale mixed-reality experience for the Meta Quest 3S using the DreamPark SDK. The full setup guide is in **README.md** at the root of the repo — but the 10 steps below will get you from zero to "I can see passthrough in my headset and I'm ready to build."

**Before you start, you'll need:**

- A **Meta Quest 3S** in **Developer Mode**, paired to your Meta account.
- A **USB-C data cable** (the one in the Quest box works).
- About **30 GB free disk space**.
- **GitHub Desktop** (or any Git client) — free at [desktop.github.com](https://desktop.github.com).
- **Unity Hub** — free at [unity.com/download](https://unity.com/download).

**Your first 10 steps:**

1. **Install Unity** — In Unity Hub → Installs → Install Editor, pick version **`6000.0.39f1`** (exact version matters). On the modules screen, check **Android Build Support** along with **OpenJDK** and **Android SDK & NDK Tools**.
2. **Get the SDK onto your computer** — In GitHub Desktop: File → Clone Repository → URL, paste the DreamPark SDK URL, pick a folder, click Clone.
3. **Open the project in Unity** — Unity Hub → Add → Add project from disk, pick the cloned folder, then click it to open. First open downloads packages and takes several minutes; let it finish before doing anything.
4. **Switch the build target to Android** — File → Build Profiles → Android, set Texture Compression to ASTC, click Switch Platform.
5. **Confirm Player settings** — Edit → Project Settings → Player → Android tab → Other Settings: Min API 32, Target API 34, Scripting Backend IL2CPP, Architecture ARM64.
6. **Confirm XR settings** — Edit → Project Settings → XR Plug-in Management → Android: enable OpenXR and Oculus. Under OpenXR → Feature Groups, enable Meta Quest Support, Hand Tracking Subsystem, and Meta XR Foundation.
7. **Sync tags & layers** — Top menu: **DreamPark → Sync Tags & Layers from Core**. (Skip this and physics won't behave correctly.)
8. **Check for SDK updates** — Top menu: **DreamPark → Check for SDK Updates**. Accept anything offered.
9. **Open the editing scene** — In the Project window, navigate to `Assets/Content/YOUR_GAME_HERE/1. Scenes/` and double-click `Template.unity`. It already has an example Attraction and Prop wired up.
10. **Plug in your Quest 3S and press Play** — Connect via USB-C, put the headset on, hit **Allow** on the USB Debugging prompt, then click Play in Unity. You should see passthrough, hand tracking, and an FPS counter in the corner.

That's it — you're ready to build.

**For the full guide and what comes next:**

- **`README.md`** at the project root — the full step-by-step setup walkthrough, plus a troubleshooting section for the things that commonly trip people up.
- Once the project is open, **DreamPark → Open Manual** in the Unity top menu — the full authoring guide for assets, props, attractions, scripting, multiplayer, and publishing.

Welcome — and have fun.

— The DreamPark Team

---
