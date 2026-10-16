# DreamPark content releases

This Unity project publishes a game or experience to the DreamPark platform through the DreamPark SDK's
production Content Uploader machinery.

## Content release workflow

Use the Unity project at this repository root. Use the official Unity CLI and the typed DreamPark command
`dreampark_content_publish`; do not automate Editor GUI clicks, recreate the uploader over raw HTTP, or use
an arbitrary shell script. Every Unity CLI request must specify this exact project path so the workflow remains
safe when multiple Unity Editors are open.

Choose the project's intended package under `Assets/Content` as the content ID. Ignore SDK-owned samples and
reserved folders. If there is no unambiguous project package, ask the release owner to choose it in ClaudeBots
rather than guessing. Use the `experimental` channel. Use upload scope `all` for a project's first complete
release and `patch` for normal updates; ask before using `code-only`. Android and iOS Addressables are standard.
macOS and Windows are opt-in. Release notes are required for every run.

## Readiness and authentication

Before activation, verify that `dreampark_content_publish` is discoverable and that the pinned
`com.unity.pipeline` package is installed. If either is missing, stop and report the setup requirement; do not
substitute a legacy uploader or GUI automation.

An agent authenticates with its own **agent key**, never a human's session, and only after a human approves it.
The flow is request → wait → publish. Run `dreampark_agent_request --content_id <id>` (for a title that does not
exist yet, add `--email <the human who will own it>`); DreamPark emails that human an Approve link. Tell them to
click it, then run `dreampark_agent_wait`, which finishes on its own once they approve (re-run it if it returns
`pending`) and stores a scoped, revocable agent key for the agent's own account, which joins the content's team,
in this project's gitignored `Library/DreamParkAgent/agent-key.json`. That file keeps one key per approved title:
approve each title once, and a later approval for another title never overwrites it. Content commands use the key
approved for their `--content_id` (or its `<id>Beta` twin). Several titles may be pending at once; one
`dreampark_agent_wait` waits on all of them. Check the keys with `dreampark_agent_status`; revoke one title's key with
`dreampark_agent_signout --content_id <id>`, every key with `dreampark_agent_signout`, or at
dreampark.app/developer → Agent keys.

Agents never ask for, type or relay the 6-digit code in that email. It exists only so the owner can run
`dreampark_agent_verify` themselves if the link can't be used (the request result's `fallback` field has the
exact command). Always give a human a command on ONE line. Agents must never print the agent key or the poll
token, and must never use the Unity sign-in window (`DreamPark → Sign In`) on a human's behalf. A human may
instead sign in through the SDK's email-code window (`dreampark_auth_open`).

`dreampark_content_publish --target beta` publishes to the separate `<content ID>Beta` target instead of the live
content ID; the default is `--target release`. A title with no backend record yet is created by its first
upload, which needs `--name` and `--description` (the store-facing metadata). New titles normally go
**beta-first**: `--target beta --mode all` creates the title record without a build and uploads only to
`<content ID>Beta`, so nothing reaches the live content ID until a later `--target release`.
Agents start a publish with `--wait false` and follow it with `dreampark_content_publish_status` until it reports
`succeeded` (with the verified version) or `failed`, because a build and upload outlast a single CLI call.

Run the complete strict DreamPark content preflight before proposing a ship. Errors, blockers, and skipped
checks stop the release. Warnings also stop it unless the release owner reviews the exact warnings and supplies
an explicit override reason.

## Approval and verification

Drafting or activating this workflow is not approval to publish. For each release, first prepare an immutable
run and show the release owner the exact content ID, channel, upload scope, platforms, release notes, checks,
and destination. Publish only after the separate ship approval. After upload, read the DreamPark backend record
back and verify the exact version created; an unverified upload is not a successful release.

For a practice run, create or update only disabled targets and draft workflows and report missing setup. Do not
activate, save scenes, build, authenticate, upload, reserve a version, or otherwise change remote state.

## Releasing a networked title on the October 2026 SDK

The October 2026 SDK re-keys NetIds (every Sequence level and the Game Manager get a scope; ids mix in the title) and
bumps the session protocol, so headsets on an older app and on the new app never join one session. Before the first
release of a networked title on it:

- A title cannot require an app version (there is no per-title minimum). The new APIs and protocol exist only in the
  DreamPark app release that carries them; DreamPark marks that build required, which tells headsets to update
  (managed venue headsets get a grace period). Publish a title that depends on `dp.shared_state` / `dp.net_body` /
  `dp.hold` after that app release is out, and say so in your release notes.
- Move hand-rolled leader/clock/snapshot/prop-sync code to the platform APIs, or at least delete `explicitId`s on
  attraction content (MULTIPLAYER.md §11). Keep `explicitId` only on a `Player.prefab` bus.
- Run all simulated-peer scenarios with your params (MULTIPLAYER.md §7), then one two-headset late-join test, and put
  the result in the release notes.
- Hand-held props: `HandheldKit.ValidateRig` must PASS and both hands were tried on a Quest (HANDHELD.md §9).
- XLua wrappers (`Assets/DreamPark/ThirdParty/XLua/Gen/`) are compiled into the app, not shipped with content. Lua that
  reaches C# members added in October 2026 (portals, walls, `DreamParkPackageHost`, `DreamParkGestures`) through `CS.*`
  works on device only on the app release built with the regenerated Gen/; prefer the `dp.*` functions, which need no
  wrapper.
- World-space TextMesh Pro text: run `DreamPark → Troubleshooting → Use Occluded TextMesh Pro Shaders` (or fix the
  `meta-occlusion` warnings) so text is occluded on older apps too.
