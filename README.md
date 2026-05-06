# DreamPark SDK: Getting Started

Welcome! Below are the steps to get from this repo to your first park playing on a Quest 3S.

## What you need

A **Meta Quest 3S** in **Developer Mode**, a **USB-C data cable**, and [Unity Hub](https://unity.com/download).

## Steps

1. **Install Unity**: through Unity Hub, install version **`6000.0.39f1`** with **Android Build Support** (plus its OpenJDK and Android SDK & NDK Tools sub-modules) and **iOS Build Support**.
2. **Clone this repo** to your computer.
3. **Open the project in Unity**: Unity Hub → Add → pick the cloned folder. Let packages finish resolving (takes a few minutes), then give your park an ID in the popup that appears. This renames the placeholder content folder to your name.
4. **Sign in**: **DreamPark → Sign In**. New here? Click **Sign Up** in the popup to create your creator account, then come back and sign in.
5. **Open the example scene**: `Assets/Content/<your park ID>/1. Scenes/Template.unity`. It has an example **Attraction** and **Prop** wired up. Poke at them to see how the SDK fits together.
6. **Verify your headset**: plug in the Quest 3S via USB-C, accept "Allow USB Debugging?" inside the headset, hit **Play** in Unity, and confirm passthrough and hand tracking work.
7. **Upload your first version**: **DreamPark → Content Uploader**, click **Upload Content (Build & Push)**. Your whole content folder gets bundled, but only **Props** and **Attractions** are surfaced in the **Level Builder** in the DreamPark iOS app.
8. **Play your park**: open the **DreamPark iOS app** (private beta on TestFlight; email **aidan@dreampark.app** for an invite) and toggle on **Beta Mode** in your park settings. This is what lets you access content you just uploaded.

That's the full loop. Iteration from here is: edit → Content Uploader → Build & Push → reopen on the Quest.

## If something went wrong

- **"Editor version not found" in Unity Hub**: `6000.0.39f1` isn't installed. Install it.
- **Errors in console on first open**: let Unity finish downloading packages. If it's still broken, close Unity, delete the project's `Library` folder, and reopen.
- **Quest doesn't show up when you hit Play**: Developer Mode on, USB-C cable supports data, and you accepted the USB Debugging prompt inside the headset.
- **Sign-in fails**: reset your password from the **Sign Up** link in the popup, then reopen **DreamPark → Sign In**.
- **Upload fails partway through**: usually a network blip, retry. If it persists, check the Unity Console for the specific error.
- **Park doesn't appear in the DreamPark iOS app**: make sure **Beta Mode** is toggled on in your park settings (uploaded content is gated behind it), and confirm you're signed into the same account as the Content Uploader.
- **Content loads in mobile but isn't working as expected**: if you've introduced new C# scripts, those need to be manually approved in a future app release before they'll run on device. **XLua** is recommended for fast, seamless code changes.
