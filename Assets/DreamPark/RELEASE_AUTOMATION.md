# DreamPark release automation

DreamPark SDK release behavior is exposed as typed Unity Pipeline commands. Install the pinned
`com.unity.pipeline` package in the Unity project, open that project, and inspect the live contract:

```sh
unity command --project-path /absolute/project/path --query dreampark --detail full --format json
```

New clones also include `/RELEASE.md`, a human-readable ClaudeBots release specification. Attaching the
repository as a ClaudeBots workspace makes it discoverable; an assigned release agent reads it and proposes
a disabled content target and draft workflow around `dreampark_content_publish`. ClaudeBots verifies the
source digest and typed translation before importing. The specification intentionally omits the project-specific
content ID and all authentication data, which must be connected and verified locally before activation.

Commands are project-scoped, so multiple open Editors are safe when every invocation includes
`--project-path`.

## Authentication

- `dreampark_auth_status` reports whether this Editor has a valid local session. It never returns the token.
- `dreampark_auth_open` opens the existing passwordless sign-in window in the selected Editor. The human
  enters the emailed code in Unity; agents never receive the code or bearer token.

## Content releases

- `dreampark_content_inspect --content_id ID` reads local package facts and the current backend record.
- `dreampark_content_preflight --content_id ID` runs the complete strict checker without changing the project
  and fails when an open scene has unsaved changes. Publishing reruns it with `--save_scenes true`.
  Automation fails closed on blocked findings, checker errors, and skipped checks. Warnings require both
  `--allow_warnings true` and a human-reviewed `--override_reason`.
- `dreampark_content_publish --content_id ID --release_notes "..."` saves, preflights, builds Android and
  iOS Addressables, uploads through the production content API, and reads the backend record back before
  reporting verification. `--mode` accepts `all`, `patch`, or `code-only`; macOS and Windows content
  targets are opt-in.

## SDK releases

- `dreampark_sdk_preflight --version X.Y.Z --release_notes "..."` verifies local ordering and backend
  admin authorization without changing files.
- `dreampark_sdk_publish --version X.Y.Z --release_notes "..."` writes the version resource, exports the
  unitypackage, uploads it, and verifies the public SDK manifest. Failure rolls the local version resource
  back; success deliberately leaves the version change for review and commit.

Publishing commands still belong behind a host-level ship approval. A successful preflight is evidence,
not authorization to publish.

## Release tokens

`DreamPark ▸ Generate Release Token` (`ReleaseTokenPanel.cs`) mints an `rlt_...` bypass-login credential
for a headless/CI caller — a human still triggers the mint from inside Unity using their own logged-in
session, but the resulting token lets automation authenticate afterward without an interactive
email-code sign-in. Three scopes, one window:

- **core** — admin-only. Meant for the Quest APK release workflow (today driven entirely by the release
  host's own `signed-upload` adapter against `/api/releases/apk/*`, with no live Unity process in the
  loop — see Ship's finding in the "Release Tokens" team thread).
- **sdk** — admin-only. Meant for `dreampark_sdk_publish`.
- **content** — the content's PRIMARY owner only (`content.contentOwner`, falling back to
  `contentOwners[0]` for older docs), not `AdminState.IsAdmin` and deliberately narrower than the full
  `contentOwners[]` collaborator list the normal release routes accept — minting a bypass-login
  credential is a bigger blast radius than using those routes yourself while logged in. This scope is
  offered to any logged-in user and refused server-side (`NOT_CONTENT_OWNER`) rather than hidden
  client-side, since ownership is per-title, not a bit AdminState can cache.

The panel calls `POST /api/release-tokens {scope, contentId?, label?, expiresInDays?}` and shows the
returned token exactly once — the server stores only its hash, so there is no "view token" endpoint and
never will be. `ReleaseTokenAPI.cs` and `ReleaseTokenPanel.cs` are meant to be byte-identical in
dreampark-core, same convention as `AdminState.cs`: minting any scope's token is just an authenticated
backend call, so it doesn't need the target repo's own code open. Contract: `docs/contracts/release-tokens.md`
in DreamPark-Web.

`dreampark_content_preflight` / `dreampark_content_publish` / `dreampark_sdk_preflight` /
`dreampark_sdk_publish` all take an optional `--release_token` (falling back to the
`DREAMPARK_RELEASE_TOKEN` environment variable), scoping `AuthAPI.GetUserAuth()` to that token for the
duration of the call via `AuthAPI.WithReleaseToken`/`ResolveReleaseToken` instead of the human session —
deliberately NOT EditorPrefs, since that store is machine-global and this credential is meant to sit on
a CI box or shared machine other tooling also touches.

**Still not end-to-end**: the backend does not yet accept a release token at the routes these commands
call. Per `docs/contracts/release-tokens.md`: "no route resolves an `rlt_...` token to `req.user`,
`verifySessionBearer` doesn't recognize the prefix" — the mint/list/revoke endpoints exist, but the
`verifySessionOrReleaseToken` OR-check at the `/api/releases/*`, `/api/sdk/publish`, and
`/api/content/*` mount points is a separate, unbuilt piece. Passing `--release_token` today reaches the
backend and gets refused as an invalid session, same as no credential at all — this SDK-side wiring is
necessary but not sufficient until that OR-check ships.
