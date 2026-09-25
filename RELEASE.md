# DreamPark content releases

> Looking for how to release the SDK package itself (the thing this repo distributes to
> other creators), not a game built with it? See [`SDK_RELEASE.md`](./SDK_RELEASE.md) instead.

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

DreamPark authentication uses the SDK's email-code sign-in window. A human enters the email and code in Unity.
Agents must never ask for, receive, copy, store, or print the code, bearer session, or other credentials.

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
