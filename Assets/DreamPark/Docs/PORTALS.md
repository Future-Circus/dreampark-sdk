# Portals

Showing a big world through a wall, floor or doorway.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

## Portals: a window into another place (Oct 2026)

**A portal is a MATERIAL on any surfaces, sampled in SCREEN space.** Use it to show a big world (a golf course, a canyon, outer space) through a wall, the floor, a doorway or several of them at once, at real scale and in true stereo 3D, inside a small real room. `Assets/DreamPark/Scripts/Features/Portals/`, shader `DreamPark/Portal` (`Assets/DreamPark/Shaders/DreamPark-Portal.shader`). The pieces:

- **`PortalScene`** goes on a GameObject that holds the far world (terrain, sky, props). Everything rendered under it is **never drawn by any play-space camera**: not the headset eyes, the phone, or the phone/Quest diorama. It is drawn only into portals and by cameras with a `PortalSceneCamera`. So the real room's depth (Meta occlusion of floors and walls) can never hide it, it can't take over a diorama, and the portal works from anywhere it is seen, inside your attraction or outside it.
  - `source` is the play-space frame the scene is mapped from (default: the parent). With `followSource` the scene copies the source's pose every frame, so author the far world in the source's local coordinates: offset 0 lines it up 1:1 with the room.
  - `offset` puts the scene that many metres away. Use it only when the scene has colliders, lights or audio that must not reach the room, and keep it ≤ 4 km: beyond that 32-bit floats step by more than 0.5 mm and the scene jitters.
  - `layer` (default 16 "Portal") is the only layer the portal camera culls.
  - `emptyIsPassthrough` (default on): wherever the scene draws nothing, the portal shows the REAL world (passthrough on a headset, the camera feed on a phone), never a flat colour. That covers a scene shrinking away in a teleport iris, one not streamed in yet, or one switched off. The scene's opaque materials must write alpha 1 (DreamPark shaders do).
  - `background` is the clear colour, used only with `emptyIsPassthrough` off.
  - `farClip` is the portal camera's far plane. The viewer's own far plane is ignored, so a phone's 50 m far plane never cuts a 900 m course.
  - `resolutionScale` / `maxResolution` / `fullResDistance` / `minDistanceScale` control quality and cost.
- **`PortalSurface`** and a `DreamPark/Portal` material go on every mesh that is a window into that scene: a wall window, the floor, both (an L), a dome. All surfaces of one scene sample ONE per-eye view, so where a wall meets a floor there is no seam.
  - The surface is ordinary opaque content at queue Geometry-4 (before the SDK's depth-only floors). It writes depth, so things in front of it draw normally and things behind it are hidden, like a real window.
  - It is Meta-occluded: real hands and legs in front of it cut it. `_MetaDepthBias` (default 0.06) keeps the real wall or floor it lies on from eating it. On a floor portal, also put a `DepthMask` mesh on the opening.
  - `PortalSurface.fade` (0..1, from Lua or C#) dissolves the surface per pixel to the real world and back (1 = there). Use it for comfortable transitions, such as letting a floor portal dissolve while the scene moves.
  - `_FallbackColor` / `_FallbackTex` (mesh UVs) show where no portal texture exists: the Scene view, catalog previews, an app older than the portal renderer.
- **Crossers** (`PortalScene.crossers` + `playVolumes`) are play-space objects that must also be seen through the portal, such as a ball hit out through the wall. The portal always draws them. Play-space cameras draw them only while their bounds centre is inside an ACTIVE play-volume box (a transform whose local unit cube is the room), so the ball flies out through the window seamlessly and never floats beyond the real wall.
  - Scene objects that stand wholly inside an active play volume (a pin on the green under your feet) are drawn in the room too.
- **`PortalSceneCamera`** on any extra camera (a shot cam, a minimap) lets it see the listed scenes (empty list = all) while it renders, including `Camera.Render()` from Lua.

**How it renders (`PortalRenderer`, automatic).** Once per frame, after tracking (`Application.onBeforeRender`), for every enabled game camera and every scene whose surfaces it can see:

- One hidden camera renders the scene from that eye's exact pose and projection. That is each XR eye (head pose this frame × the OpenXR eye offset; single-pass instanced and multiview), the phone camera, or the phone diorama camera with its depth-scaled projection.
- The pose and projection are mapped into the scene and cut to the screen rectangle the surfaces cover.
- The result goes into a pooled texture whose pixels match the eye buffer's, so it stays sharp with no resampling blur. It is capped at `maxResolution` and its resolution falls with distance.

Cost control:

- Nothing renders for a camera that sees no surface (frustum + facing).
- There is one render per eye per scene, however many surfaces share it.
- No shadows, post-processing, depth/opaque textures or render scale.
- Meta occlusion keywords are off for that render.
- Textures are freed after about 2 s unused.

Kill switch: `PortalRenderer.Enabled`. `PortalRenderer.Last` holds the last frame's renders, pixels, CPU ms and texture memory.

**Rules.**

- Do not write `Renderer.forceRenderingOff` on scene renderers or crossers; the scene owns it. Use `SetActive` / `enabled`.
- Keep scene geometry behind the wall and below the floor in mapped space. There is no single-plane clipping (it can't serve an L-shaped portal), so anything of the far world standing inside the room shows on the surfaces behind it. Cull it, or let it be a real in-room object.
- New portal C# ships in the app: content using portals needs an app build that has them.

Typical layout: the far world under one `PortalScene` object (e.g. `CourseScene`), and one `PortalSurface` mesh per window (e.g. `Surface_Wall`, `Surface_Floor`).
