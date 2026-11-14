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
- **Crossers** are play-space objects that must also be seen through the portal: a zombie walking out of a door, a ball hit out through the wall, a box sliding down through a floor opening.
  - **Opening crossers (use these).** Register the object against the ONE opening it passes through, and it is split at that opening's plane, per pixel: the room's cameras draw the part on the room side, the portal draws the part beyond it. Half through a door it is one continuous body, with no pop and no double. Each crosser clips against its own opening, so an L-shaped portal (wall + floor) works: register the box against the floor surface and the walker against the wall surface. In an `offset` scene the part beyond is drawn mapped into the scene (`PlayToScene`), so the object stays where it really is in play space (its colliders, AI and navmesh untouched) and still shows through the door. Nothing is baked or copied.
    - C#: `scene.AddCrosser(crosserTransform, surface)` (a `PortalSurface`) or `scene.AddCrosser(crosserTransform, openingTransform)`, `scene.RemoveCrosser(t)`, `scene.IsCrossing(t)`, or the inspector list `PortalScene.openingCrossers`. `PortalScene.Cross(go, openingGo, on[, scene])` does the lookup for you.
    - Lua: `dp.portal_cross(go, opening[, on[, scene]])`. `opening` is the PortalSurface's object (or an object with one under it); `on` defaults to true; `false` drops it from every opening. Calling it again with another opening moves it there. Guard with `if dp.portal_cross then` (older apps lack it).
    - The opening's plane: a planar `PortalSurface` uses its front (the side that faces the room). An unreadable mesh uses its thinnest axis, turned toward the room. Any other transform uses its +Z (blue axis), which must point into the room.
    - Register it before it reaches the opening and leave it registered while it is near the opening. When it is wholly in the room, the room draws it as usual and the portal skips it. When it is wholly beyond the opening, only the portal draws it. Neither case clips anything. Only a renderer that straddles the plane (bounds plus a little slack), or one drawn into an offset scene, gets the clip.
    - The clip lives in the SDK shaders: `DreamPark-UniversalShader`, `DreamPark-Unlit` and `DreamPark/Particles`. That covers skinned meshes, mesh renderers and particle systems (world or local simulation), in single-pass instanced / multiview XR and on phones, with Meta occlusion unchanged. A crosser on any other shader is drawn whole on both sides while it straddles.
    - The values travel in the renderer's `MaterialPropertyBlock` (`_DPCross`, `_DPCrossPlane`, `_DPCrossMap`), written only while the renderer straddles or is mapped. A block of your own is kept: the clip values are written into it, so re-apply your values with Get/SetPropertyBlock and never replace the block wholesale while the renderer crosses. Per-material blocks (`SetPropertyBlock(block, index)`) are not clipped. In an offset scene, a crosser's world-space `Renderer.bounds` override is replaced during the portal render.
    - Needs an app with the crosser renderer (it ships in the app). The shaders do nothing on an older app.
  - **Volume crossers** (`PortalScene.crossers` + `playVolumes`, the older form, unchanged). The portal always draws them. Play-space cameras draw them only while their bounds centre is inside an ACTIVE play-volume box (a transform whose local unit cube is the room). The object moves whole from one side to the other as its centre crosses, so a big object pops at the opening, and nothing is mapped into an `offset` scene. Fine for a small fast ball. Use opening crossers for anything a player watches cross.
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
- Keep scene geometry behind the wall and below the floor in mapped space. The scene itself is never clipped at a plane (one plane can't serve an L-shaped portal; only opening crossers are split, each at its own opening), so anything of the far world standing inside the room shows on the surfaces behind it. Cull it, or let it be a real in-room object.
- New portal C# ships in the app: content using portals needs an app build that has them.

Typical layout: the far world under one `PortalScene` object (e.g. `CourseScene`), and one `PortalSurface` mesh per window (e.g. `Surface_Wall`, `Surface_Floor`).
