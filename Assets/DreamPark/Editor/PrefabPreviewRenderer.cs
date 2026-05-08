#if !DREAMPARKCORE
using UnityEngine;
using UnityEditor;

namespace DreamPark
{
    public static class PrefabPreviewRenderer
    {
        private static readonly Vector3 kIsolatedPosition = new Vector3(10000, 10000, 10000);
        private const float kElevationAngle = 30f;
        private const float kAzimuthAngle = 45f;
        // 1.03x = ~3% breathing room around the projected silhouette. We can
        // afford to be this aggressive because:
        //   1. Bounds are camera-aligned projected extents, not bounding-sphere
        //      radius — there's no slack from "round corners of a sphere".
        //   2. SkinnedMeshRenderers use mesh.bounds (tight bind-pose AABB),
        //      not the renderer's animation-padded localBounds.
        // The user's review notes the previews still felt small at 8% padding.
        private const float kPaddingMultiplier = 1.03f;

        public static Texture2D RenderPreview(GameObject prefab, int resolution = 512)
        {
            // Instantiate at isolated position so it doesn't interfere with scene
            GameObject instance = Object.Instantiate(prefab, kIsolatedPosition, Quaternion.identity);
            instance.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                // Calculate combined bounds from all renderers in the hierarchy
                Bounds bounds = CalculateBounds(instance);
                if (bounds.size == Vector3.zero)
                {
                    Debug.LogWarning($"PrefabPreviewRenderer: No renderers found on {prefab.name}");
                    return null;
                }

                // Create render texture. We DON'T enable MSAA on the RT —
                // URP's back-buffer is 4-sample by default and our 8-sample
                // request raised "Attachment 0 was created with 4 samples
                // but 8 samples were requested". Anti-aliasing comes from
                // supersampling instead: render at 2x and downsample below.
                int superSampled = resolution * 2;
                RenderTexture rt = new RenderTexture(superSampled, superSampled, 24, RenderTextureFormat.ARGB32);
                rt.antiAliasing = 1;
                rt.Create();

                // Create temporary camera
                GameObject camObj = new GameObject("PreviewCamera") { hideFlags = HideFlags.HideAndDontSave };
                Camera cam = camObj.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.targetTexture = rt;
                cam.nearClipPlane = 0.01f;
                cam.fieldOfView = 30f;
                cam.allowMSAA = false;
                // Tells URP to treat this as a preview render — skips some
                // of the elaborate features (post-processing stack, render
                // graph passes) that don't apply to a one-shot thumbnail.
                cam.cameraType = CameraType.Preview;

                // Position camera at 3/4 perspective angle
                PositionCamera(cam, bounds);

                // Create temporary lighting
                GameObject lightObj = new GameObject("PreviewLight") { hideFlags = HideFlags.HideAndDontSave };
                Light light = lightObj.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                light.color = Color.white;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                // Add fill light from below-left
                GameObject fillLightObj = new GameObject("PreviewFillLight") { hideFlags = HideFlags.HideAndDontSave };
                Light fillLight = fillLightObj.AddComponent<Light>();
                fillLight.type = LightType.Directional;
                fillLight.intensity = 0.4f;
                fillLight.color = new Color(0.8f, 0.85f, 1f);
                fillLight.transform.rotation = Quaternion.Euler(-20f, 150f, 0f);

                // Render
                cam.Render();

                // Downsample 2x → resolution for cheap, high-quality AA.
                // Blit through a non-MSAA RT at the target size; bilinear
                // filtering averages the 4 source pixels per destination
                // pixel, which gives a result comparable to 4x MSAA but
                // works in any render pipeline. Avoids the MSAA-attachment
                // mismatch we had with rt.antiAliasing = 8.
                RenderTexture downRt = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGB32);
                downRt.filterMode = FilterMode.Bilinear;
                Graphics.Blit(rt, downRt);

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = downRt;

                Texture2D result = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                result.Apply();

                RenderTexture.active = previous;

                // Cleanup
                Object.DestroyImmediate(camObj);
                Object.DestroyImmediate(lightObj);
                Object.DestroyImmediate(fillLightObj);
                RenderTexture.ReleaseTemporary(downRt);
                rt.Release();
                Object.DestroyImmediate(rt);

                return result;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(root.transform.position, Vector3.zero);

            Bounds bounds = new Bounds();
            bool initialized = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];

                // Skip transient / non-visual renderers — particle systems
                // haven't spawned anything at preview-render time, trail
                // renderers have no length yet, etc.
                if (r is ParticleSystemRenderer) continue;
                if (r is TrailRenderer) continue;
                if (r is LineRenderer) continue;
                if (r is BillboardRenderer) continue;

                Bounds rb = GetRendererBoundsRobust(r);
                if (rb.size == Vector3.zero) continue;

                if (!initialized) { bounds = rb; initialized = true; }
                else bounds.Encapsulate(rb);
            }

            // Last-resort: if every renderer reported zero (e.g. all inactive
            // and all using shaders we can't introspect), fall back to the
            // first one's reported bounds — better than failing entirely.
            if (!initialized)
            {
                bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        // Picks the tightest world-space AABB we can compute for a renderer.
        //
        // For SkinnedMeshRenderers we ALWAYS prefer mesh.bounds (the bind-
        // pose AABB) over Renderer.bounds. Reason: SkinnedMeshRenderer.localBounds
        // is typically padded by Unity to cover the worst-case pose across
        // every animation in the rig, so using r.bounds for a static preview
        // frames for the imaginary "arms outstretched in all directions"
        // silhouette and leaves the actual rendered character hugging the
        // middle of the image. mesh.bounds is the geometry's real AABB.
        // P_Skeleton and P_BeetleBug were the canonical examples — both
        // looked tiny at the previous 1.08 padding.
        //
        // For everything else we use Renderer.bounds, which is correct and
        // tight for static meshes. We fall back to MeshFilter.sharedMesh.bounds
        // only when Renderer.bounds returns zero — that happens for inactive
        // GameObjects (TreasureChest's optional contents, etc.).
        private static Bounds GetRendererBoundsRobust(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
            {
                return TransformLocalBounds(smr.sharedMesh.bounds, r.transform);
            }

            Bounds b = r.bounds;
            if (b.size != Vector3.zero) return b;

            // Fallback for inactive MeshRenderers — bounds return zero when
            // the GameObject is inactiveInHierarchy.
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    return TransformLocalBounds(mf.sharedMesh.bounds, r.transform);
            }
            return b;
        }

        private static Bounds TransformLocalBounds(Bounds local, Transform t)
        {
            Vector3 c = local.center;
            Vector3 e = local.extents;

            // Transform the 8 corners into world space and rebuild an AABB.
            // Cheap (8 matrix multiplies) and exact for any rotation/scale.
            var wc = t.TransformPoint(c);
            var b = new Bounds(wc, Vector3.zero);
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 corner = c + new Vector3(sx * e.x, sy * e.y, sz * e.z);
                b.Encapsulate(t.TransformPoint(corner));
            }
            return b;
        }

        private static void PositionCamera(Camera cam, Bounds bounds)
        {
            Vector3 center = bounds.center;

            // Convert spherical coordinates (elevation, azimuth) to direction
            float elevRad = kElevationAngle * Mathf.Deg2Rad;
            float azimRad = kAzimuthAngle * Mathf.Deg2Rad;
            Vector3 direction = new Vector3(
                Mathf.Cos(elevRad) * Mathf.Sin(azimRad),
                Mathf.Sin(elevRad),
                Mathf.Cos(elevRad) * Mathf.Cos(azimRad)
            ).normalized;

            // Camera basis: cam looks toward `center`, so its forward is the
            // negation of `direction`. Build right and up from that.
            Vector3 forward = -direction;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            // Guard against a degenerate cross product (looking straight up
            // or down) — fall back to world-X.
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            Vector3 up = Vector3.Cross(forward, right).normalized;

            // Project the bounds onto the camera's right/up/forward axes.
            // The half-extent along an axis A is |A·extents|, computed
            // component-wise (this is the standard AABB-onto-axis projection).
            Vector3 ext = bounds.extents;
            float halfWidth  = Mathf.Abs(right.x   * ext.x) + Mathf.Abs(right.y   * ext.y) + Mathf.Abs(right.z   * ext.z);
            float halfHeight = Mathf.Abs(up.x      * ext.x) + Mathf.Abs(up.y      * ext.y) + Mathf.Abs(up.z      * ext.z);
            float halfDepth  = Mathf.Abs(forward.x * ext.x) + Mathf.Abs(forward.y * ext.y) + Mathf.Abs(forward.z * ext.z);

            // Distance needed to fit the larger of width/height in our FOV.
            // The output RT is square (resolution × resolution) so vertical
            // and horizontal FOV are equal — we just take the max half-extent.
            float halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float fitExtent = Mathf.Max(halfWidth, halfHeight);
            float distance = (fitExtent / Mathf.Tan(halfFov)) * kPaddingMultiplier;

            // Push back by halfDepth so the front face of the bounds doesn't
            // cross the near plane — without this, large flat objects can
            // get clipped on the closer side.
            distance += halfDepth;

            cam.transform.position = center + direction * distance;
            cam.transform.LookAt(center);
            cam.farClipPlane = distance + halfDepth * 2f + 10f;
        }
    }
}
#endif
