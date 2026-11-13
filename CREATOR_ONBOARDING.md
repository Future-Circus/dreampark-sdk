# Creator onboarding (SDK template)

For a creator starting a new title from this template repo. Not shipped to game projects; game agents read
`CLAUDE.md` (the game agent guide) and the reference docs in `Assets/DreamPark/Docs/`.

## What This Is
SDK template for third-party game developers. A complete Unity 6.3 LTS (6000.3.23f1) project. Creators fork this repo and place their game content in Assets/Content/{GameName}/.

## Template Structure
```
Assets/
├── [StartHere].unity  ← Startup scene, auto-opened on first project open (StartupSceneOpener,
│                        configured in Assets/.dreampark-editor.json, mode FirstOpenOnly;
│                        re-point via DreamPark → Startup Scene...). Meta building blocks
│                        pre-wired + Player.prefab and Attraction.prefab instances.
├── DreamPark/         ← SDK source. SDK-owned: replaced on every SDK update, so never
│                        edit it for one title.
├── Content/
│   ├── Sample/                ← Bundled worked example (2 attractions, 5 props, Sample.unity,
│   │                            8 Lua scripts). Deliberately NOT gated for browsing/editing;
│   │                            RESERVED only in that it is never the creator's content and
│   │                            cannot be published. See ContentFolders.IsSample.
│   └── YOUR_GAME_HERE/        ← Renamed via the in-editor setup popup (ContentIdSetupPopup, auto-opened by PlaceholderContentDetector; letters+digits, starts with a letter, 2–64 chars). Folder name = content ID. Also reserved until renamed. Multiple content folders may coexist — each is an independent package; the Content Uploader's dropdown picks which to publish.
│       ├── Prefabs/            ← Player.prefab + Attraction.prefab ship in the template;
│       │                         Phone Player.prefab = the optional phone rig (built by
│       │                         Tools/Builders/PhonePlayerBuilder.cs; used only when the
│       │                         Phone Player slot is set — see § Phone players & gestures);
│       │                         A_*.prefab attractions (AttractionTemplate root),
│       │                         P_*.prefab props (PropTemplate root);
│       │                         Game Container.prefab = the Game Manager (generated once)
│       ├── DreamSequence/      ← Start / Overlay / Game Over Level prefabs (creator-owned)
│       │                         + generated Dream Sequence Package.asset (see § Packages)
│       ├── .dreampark-*.json   ← package authoring (library, arena, dream-sequence,
│       │                         sequence = ADVENTURE). Commit them.
│       ├── Previews/           ← {prefabName}.png tile art
│       ├── Scripts/            ← Game-specific C# (minimal — prefer Lua) + .lua.txt
│       ├── ThirdParty/         ← Only used assets (git-tracked, shipped in builds)
│       └── ThirdPartyLocal/    ← Imported packages land here (excluded from builds by
│                                 ContentProcessor — but NOT actually gitignored, despite
│                                 what several comments in the repo claim)
```

There are no `1. Scenes/` or `2. Features/` folders — that layout predates the current template.

## SDK files and docs in your project
`Assets/DreamPark/` is SDK-owned and is replaced on every SDK update. Customize through your own content folder,
prefab-instance overrides and Lua, never by editing SDK files. `#if DREAMPARKCORE` blocks in SDK source are
DreamPark-app-only code, compiled out of the SDK; ignore them.

**These docs ship with the SDK.** In a game project, each SDK update (once per SDK version, tracked in the committed
`ProjectSettings/DreamParkSdkDocs.json`; set `"enabled": false` there to opt out) rewrites the root copies of the SDK
docs from `Assets/DreamPark/Docs/`, except the game's own `README.md`. They carry a "managed" header: don't edit them,
your edits are overwritten. It also keeps a managed `@Assets/DreamPark/Docs/CLAUDE.md` import block at the top of the
game's own `CLAUDE.md`. That block states that the project's own notes win wherever they disagree with the SDK guide,
and everything below it belongs to the game.

## Workflow
1. Creator clones dreampark-sdk as a new project.
2. On first editor open, the setup popup renames YOUR_GAME_HERE to the game name (e.g., CoinCollector). (There is no new-park.sh — the popup is the rename mechanism.)
3. Creator adds game content, Lua scripts, and prefabs to Assets/Content/{GameName}/.
4. All gameplay logic is Lua via LuaBehaviour. Not "Lua-first" — Lua. C# in `Assets/Content/` should be interop shims and nothing else, and EasyEvent components are deprecated (see above).
5. Creator authors the three packages in the Content Uploader's **Packages** section — Arena priorities, the Sequence order plus its Start/Overlay/Game Over levels, the Adventure's Start/End route — and puts title-wide rules in the Game Manager (see § Packages). Every package is optional, but the title needs at least one playable package to upload; an unfinished Sequence or Adventure is left out with a warning.
6. Built Addressable prefabs are deployed to DreamPark servers via DreamPark → Content Uploader: fill the panel's **Name** and **Description** fields (the launch window shows them read-only, and an empty Name blocks the upload), clear the pre-upload checks (see Shipping below), hit **Compile & Upload**, then **Start · All** in the launch window. One attraction at a time or a whole park's worth; each upload publishes the attractions catalog automatically and is immediately playable in the iOS app with Experimental Mode on. The launch window's **Upload Scope** picker selects All / Patch / Code-only; the first release for a content ID is locked to All, and Patch is the default choice after that. Smart (dependency-aware) bundling is the default strategy as of Aug 2026 and is what makes the partial modes work — Legacy is deprecated, hidden behind `DreamPark → Troubleshooting → Use Legacy Bundling`, and forces a full re-upload when active.
7. Sign-in (`DreamPark → Sign In`) is passwordless as of July 2026 — email + 6-digit OTP, and `/auth/otp/verify` get-or-creates the account, so there is no separate sign-up path and no password to reset.
