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
