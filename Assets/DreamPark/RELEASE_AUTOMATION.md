# DreamPark release automation

> **DEPRECATED (Oct 2026). Do not follow this file.** Agents now sign in with their own human-approved
> **agent key** and upload with `dreampark_agent_request` → `dreampark_agent_wait` (the owner clicks Approve in
> the email) → `dreampark_content_publish --target beta|release`. Agents never ask for, type or relay the
> 6-digit code. The current instructions are the project's `CLAUDE.md` → "Agent access (sign in, upload,
> website)" and `RELEASE.md` → "Readiness and authentication". Anything below that conflicts with them is out
> of date.

DreamPark SDK release behavior is exposed as typed Unity Pipeline commands. Install the pinned
`com.unity.pipeline` package in the Unity project, open that project, and inspect the live contract:

```sh
unity command --project-path . --query dreampark --detail full --format json
```

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

Publishing commands still belong behind a human ship decision. A successful preflight is evidence,
not authorization to publish.
