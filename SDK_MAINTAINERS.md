# SDK maintainers

For agents and people working on the SDK itself (this repo). Not shipped to game projects.

## The agent guide and its docs

- `CLAUDE.md` (root) is the **game agent guide**. Every game session and subagent loads it through the game's
  `@Assets/DreamPark/Docs/CLAUDE.md` import, so keep it short (target ≤ 30,000 chars), current and written for a
  game agent: no dated history, no maintainer or template-forker instructions. Positive, precise rules.
- Detail goes in a reference doc in `Assets/DreamPark/Docs/` and gets a row in the guide's reference table.
  Template/creator setup goes in `CREATOR_ONBOARDING.md`; dated change notes go in `CHANGELOG-agent-guide.md`;
  maintainer notes go here.
- **How docs reach games** (`Assets/DreamPark/Editor/DreamParkSdkDocs.cs`):
  - Files that live in `Assets/DreamPark/Docs/` ship in the `.unitypackage` as they are (the package is
    `Assets/DreamPark`). Commit a `.meta` with each new one.
  - Root docs listed in `DreamParkSdkDocs.ShippedDocs` are copied into `Assets/DreamPark/Docs/` by
    `StageForExport()` with deterministic GUIDs (MD5 of `dreampark-sdk-docs:Assets/DreamPark/Docs/<path>`).
    Anything not on that list (this file, `CREATOR_ONBOARDING.md`, the changelog, internal specs) never ships.
  - In a game, `ApplyToProject()` runs once per SDK version: it copies every `Docs/*.md` except `CLAUDE.md` and
    `README.md` to the project root with a "Managed" header, and keeps the `@Assets/DreamPark/Docs/CLAUDE.md`
    import block at the top of the game's `CLAUDE.md`.
  - **Retiring a doc:** package imports never delete. Add its exact `Assets/DreamPark/Docs/<name>.md` (and `.meta`)
    paths to a new `RetiredAssetMigration` in `SDKUpgradeCleanup.cs`. The managed root copy in each game is not
    removed by any code; delete it by hand or teach `ApplyToProject` a retired-root list.
- `Tools/agent/` (PLAYBOOK.md, STARTER_PROMPT.md, helper scripts) is local tooling (gitignored) and is not shipped.

### Package EditMode suites

From the guide's § Packages → Checks & tests: EditMode suites: `DreamParkPackageCompilerTests`, `DreamParkPackageHostTests`, `ArenaPackageStoreTests`, `ContentSequenceStoreTests`, `DreamSequenceGeneratorTests`, `DreamSequencePackageRuntimeTests`, `DreamSequenceRequiredCheckTests`, `ParkSimArenaPreviewStateTests`, `ParkSimPackageTestTests`.

### `net-budget-invariant`

`net-budget-invariant` is intentionally not creator-facing: it compares two SDK-owned
constants and is enforced by both SDK release preflight and the Editor SDK publish panel.

### XLua `Gen/` export gate

From the guide's § XLua `Gen/` is SDK-owned:

- **Every SDK export is gated** (Publish, CLI publish, and the local "Export .unitypackage"):
  codegen drift, unresolved helpers or unregistered wrappers block the export.
- Before publishing, run `python3 scripts/release/check_sdk_upgrade.py upgrade <package> <real project>`
  against an existing game project — it simulates the import and fails on anything that would not compile.
