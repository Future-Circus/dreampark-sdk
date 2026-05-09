#if !DREAMPARKCORE
using UnityEngine;
using UnityEditor;

namespace DreamPark
{
    // Two-pass preview renderer:
    //
    //   Pass 1 (scout): Render the prefab at low res (256×256) with generous
    //   framing. Scan the alpha channel to find the actual on-screen
    //   silhouette — the rectangle of non-transparent pixels. This is the
    //   ground truth for "where does this prefab actually draw" without
    //   relying on bounds math, which is unreliable for skinned meshes
    //   (Unity pads localBounds for animation), prefabs with off-center
    //   pivots, and odd renderer hierarchies.
    //
    //   Pass 2 (final): Re-render at high res (target × 2 for supersample
    //   AA) with the camera adjusted so the silhouette is centered and
    //   fills the frame to the desired fill ratio. Downsample the final
    //   render to the target resolution via Graphics.Blit.
    //
    // Why two passes instead of one-pass-render-and-crop:
    //   A single render at 3× target → alpha crop → resample-down would
    //   waste resolution whenever the silhouette ends up small in the
    //   render — e.g. a 400×400 silhouette upsampled to 512×512 looks
    //   soft. Two passes give every preview a full 512×512 of actual
    //   rendered pixels (1024×1024 supersampled), regardless of how
    //   eccentric the prefab's bounds are.
    public static class PrefabPreviewRenderer
    {
        private static readonly Vector3 kIsolatedPosition = new Vector3(10000, 10000, 10000);
        private const float kElevationAngle = 30f;
        private const float kAzimuthAngle = 45f;

        // Generous padding for the scout render so we never clip the
        // silhouette. Pass 2's framing is computed from the scout's actual
        // alpha rect, so this only needs to "include everything".
        private const float kScoutFramingPadding = 1.5f;

        // Final breathing room around the silhouette, as a fraction of the
        // longer side. 3% = nearly edge-to-edge with just enough margin
        // that the silhouette doesn't kiss the card frame.
        private const float kPostCropBreathingRoom = 0.03f;

        // Alpha threshold for considering a pixel "occupied". 5/255 catches
        // anti-aliased edges without being fooled by ~zero noise.
        private const byte kAlphaThreshold = 5;

        // Scout pass resolution. 256² is a sweet spot — fast (single ms to
        // render + scan) but high enough that even a small silhouette
        // resolves to a usable bounding rect for the corrective math.
        private const int kScoutRes = 256;

        public static Texture2D RenderPreview(GameObject prefab, int resolution = 512)
        {
            if (prefab == null) return null;

            // Instantiate at isolated position so it doesn't interfere with scene
            GameObject instance = Object.Instantiate(prefab, kIsolatedPosition, Quaternion.identity);
            instance.hideFlags = HideFlags.HideAndDontSave;

            GameObject camObj = null;
            GameObject lightObj = null;
            GameObject fillLightObj = null;
            RenderTexture scoutRt = null;
            RenderTexture finalRt = null;
            Camera cam = null;

            try
            {
                Bounds bounds = CalculateBounds(instance);
                if (bounds.size == Vector3.zero)
                {
                    Debug.LogWarning($"PrefabPreviewRenderer: No renderers found on {prefab.name}");
                    return null;
                }

                // ── Camera + lights (shared between both passes) ─────────
                camObj = new GameObject("PreviewCamera") { hideFlags = HideFlags.HideAndDontSave };
                cam = camObj.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.nearClipPlane = 0.01f;
                cam.fieldOfView = 30f;
                cam.allowMSAA = false;
                cam.cameraType = CameraType.Preview;

                lightObj = new GameObject("PreviewLight") { hideFlags = HideFlags.HideAndDontSave };
                Light keyLight = lightObj.AddComponent<Light>();
                keyLight.type = LightType.Directional;
                keyLight.intensity = 1.2f;
                keyLight.color = Color.white;
                keyLight.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                fillLightObj = new GameObject("PreviewFillLight") { hideFlags = HideFlags.HideAndDontSave };
                Light fillLight = fillLightObj.AddComponent<Light>();
                fillLight.type = LightType.Directional;
                fillLight.intensity = 0.4f;
                fillLight.color = new Color(0.8f, 0.85f, 1f);
                fillLight.transform.rotation = Quaternion.Euler(-20f, 150f, 0f);

                // ── Pass 1: scout at low res with generous framing ───────
                scoutRt = NewRenderTexture(kScoutRes);
                cam.targetTexture = scoutRt;
                FrameInfo scoutFrame = ApplyBoundsFraming(cam, bounds, kScoutFramingPadding);
                cam.Render();

                RectInt silhouette;
                {
                    Texture2D scoutTex = ReadIntoTexture2D(scoutRt, kScoutRes, kScoutRes);
                    try
                    {
                        silhouette = FindAlphaBoundingRect(scoutTex, kAlphaThreshold);
                    }
                    finally
                    {
                        Object.DestroyImmediate(scoutTex);
                    }
                }

                if (silhouette.width <= 0 || silhouette.height <= 0)
                {
                    Debug.LogWarning($"PrefabPreviewRenderer: scout render produced no opaque pixels for {prefab.name} — bounds {bounds.size}.");
                    return null;
                }

                // ── Compute corrective framing for pass 2 ────────────────
                // Goal: the final render should have the silhouette CENTERED
                // and filling (1 - 2*breathing) of the longer screen axis.
                ApplyCorrectiveFraming(cam, scoutFrame, silhouette);

                // ── Pass 2: final render at supersampled resolution ──────
                int finalRes = resolution * 2;
                finalRt = NewRenderTexture(finalRes);
                cam.targetTexture = finalRt;
                cam.Render();

                // Downsample 2× → resolution via bilinear blit. Equivalent
                // to ~4× MSAA in quality, works in any render pipeline.
                RenderTexture downRt = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGB32);
                downRt.filterMode = FilterMode.Bilinear;
                Graphics.Blit(finalRt, downRt);
                Texture2D result = ReadIntoTexture2D(downRt, resolution, resolution);
                RenderTexture.ReleaseTemporary(downRt);

                return result;
            }
            finally
            {
                if (cam != null) cam.targetTexture = null;
                if (camObj != null) Object.DestroyImmediate(camObj);
                if (lightObj != null) Object.DestroyImmediate(lightObj);
                if (fillLightObj != null) Object.DestroyImmediate(fillLightObj);
                if (scoutRt != null) { scoutRt.Release(); Object.DestroyImmediate(scoutRt); }
                if (finalRt != null) { finalRt.Release(); Object.DestroyImmediate(finalRt); }
                Object.DestroyImmediate(instance);
            }
        }

        private static RenderTexture NewRenderTexture(int size)
        {
            // No MSAA on the RT — URP's back-buffer is 4-sample by default
            // and a higher request raises "Attachment 0 was created with 4
            // samples but X samples were requested". AA comes from
            // supersampling instead (final pass renders at 2× and
            // downsamples).
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 1;
            rt.Create();
            return rt;
        }

        private struct FrameInfo
        {
            public Vector3 lookAt;
            public Vector3 directionToCamera;   // unit vector from lookAt to camera
            public Vector3 right;
            public Vector3 up;
            public float distance;              // camera distance from lookAt
            public float halfDepth;
        }

        // Frames the camera using bounds math — used for the scout pass and
        // as the basis for the corrective second pass. Returns the camera
        // basis vectors so the caller can convert pixel-space offsets back
        // into world-space corrections.
        private static FrameInfo ApplyBoundsFraming(Camera cam, Bounds bounds, float padding)
        {
            Vector3 center = bounds.center;

            float elevRad = kElevationAngle * Mathf.Deg2Rad;
            float azimRad = kAzimuthAngle * Mathf.Deg2Rad;
            Vector3 direction = new Vector3(
                Mathf.Cos(elevRad) * Mathf.Sin(azimRad),
                Mathf.Sin(elevRad),
                Mathf.Cos(elevRad) * Mathf.Cos(azimRad)
            ).normalized;

            Vector3 forward = -direction;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            Vector3 up = Vector3.Cross(forward, right).normalized;

            Vector3 ext = bounds.extents;
            float halfWidth  = Mathf.Abs(right.x   * ext.x) + Mathf.Abs(right.y   * ext.y) + Mathf.Abs(right.z   * ext.z);
            float halfHeight = Mathf.Abs(up.x      * ext.x) + Mathf.Abs(up.y      * ext.y) + Mathf.Abs(up.z      * ext.z);
            float halfDepth  = Mathf.Abs(forward.x * ext.x) + Mathf.Abs(forward.y * ext.y) + Mathf.Abs(forward.z * ext.z);

            float halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float fitExtent = Mathf.Max(halfWidth, halfHeight);
            float distance = (fitExtent / Mathf.Tan(halfFov)) * padding + halfDepth;

            cam.transform.position = center + direction * distance;
            cam.transform.LookAt(center);
            cam.farClipPlane = distance + halfDepth * 2f + 10f;

            return new FrameInfo
            {
                lookAt = center,
                directionToCamera = direction,
                right = right,
                up = up,
                distance = distance,
                halfDepth = halfDepth,
            };
        }

        // Adjusts the camera so the scout's silhouette will end up centered
        // and filling (1 - 2*breathing) of the next render's longer axis.
        //
        // Math:
        //   - World units per scout pixel = imagePlaneHeightAtCamDist / kScoutRes,
        //     where imagePlaneHeight = 2 · distance · tan(fov/2).
        //   - Pixel offset of silhouette center from scout image center,
        //     converted to world units, gives the lookAt translation needed
        //     to center the silhouette.
        //   - Zoom factor = silhouetteFraction / fillTarget. When < 1 we're
        //     zooming in (silhouette gets bigger). When > 1 we're pulling
        //     back (silhouette was already filling more than the target).
        private static void ApplyCorrectiveFraming(Camera cam, FrameInfo scout, RectInt silhouette)
        {
            float fillTarget = 1f - 2f * kPostCropBreathingRoom;
            float silhouetteFraction = (float)Mathf.Max(silhouette.width, silhouette.height) / kScoutRes;
            float zoomFactor = Mathf.Max(0.05f, silhouetteFraction / fillTarget);

            float pixCenterX = silhouette.x + silhouette.width  * 0.5f - kScoutRes * 0.5f;
            float pixCenterY = silhouette.y + silhouette.height * 0.5f - kScoutRes * 0.5f;

            float halfFovRad = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float worldImageHeight = 2f * scout.distance * Mathf.Tan(halfFovRad);
            float worldPerPixel = worldImageHeight / kScoutRes;

            Vector3 lookAtShift = scout.right * (pixCenterX * worldPerPixel)
                                + scout.up    * (pixCenterY * worldPerPixel);

            Vector3 newLookAt = scout.lookAt + lookAtShift;
            float newDistance = scout.distance * zoomFactor;

            cam.transform.position = newLookAt + scout.directionToCamera * newDistance;
            cam.transform.LookAt(newLookAt);
            cam.farClipPlane = newDistance + scout.halfDepth * 2f + 10f;
        }

        // ── Bounds math (scout-only — pass 2 uses the alpha rect) ───────
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
                if (r is ParticleSystemRenderer) continue;
                if (r is TrailRenderer) continue;
                if (r is LineRenderer) continue;
                if (r is BillboardRenderer) continue;

                Bounds rb = r.bounds;
                if (rb.size == Vector3.zero)
                {
                    rb = MeshBoundsFor(r);
                }
                if (rb.size == Vector3.zero) continue;

                if (!initialized) { bounds = rb; initialized = true; }
                else bounds.Encapsulate(rb);
            }

            if (!initialized)
            {
                bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        private static Bounds MeshBoundsFor(Renderer r)
        {
            Mesh mesh = null;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            else if (r is SkinnedMeshRenderer smr)
            {
                mesh = smr.sharedMesh;
            }
            if (mesh == null) return new Bounds(r.transform.position, Vector3.zero);
            return TransformLocalBounds(mesh.bounds, r.transform);
        }

        private static Bounds TransformLocalBounds(Bounds local, Transform t)
        {
            Vector3 c = local.center;
            Vector3 e = local.extents;
            var b = new Bounds(t.TransformPoint(c), Vector3.zero);
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 corner = c + new Vector3(sx * e.x, sy * e.y, sz * e.z);
                b.Encapsulate(t.TransformPoint(corner));
            }
            return b;
        }

        // ── Alpha rect scanning ─────────────────────────────────────────
        // Find the smallest axis-aligned rectangle that contains every
        // non-transparent pixel. ~0.5ms for 256² (the scout pass size).
        private static RectInt FindAlphaBoundingRect(Texture2D tex, byte alphaThreshold)
        {
            int w = tex.width;
            int h = tex.height;
            Color32[] px = tex.GetPixels32();

            int minY = FindFirstRowWithAlpha(px, w, h, alphaThreshold);
            if (minY < 0) return new RectInt(0, 0, 0, 0);
            int maxY = FindLastRowWithAlpha(px, w, h, alphaThreshold, minY);
            int minX = FindFirstColWithAlpha(px, w, alphaThreshold, minY, maxY);
            int maxX = FindLastColWithAlpha(px, w, alphaThreshold, minY, maxY, minX);

            return new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private static int FindFirstRowWithAlpha(Color32[] px, int w, int h, byte threshold)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                    if (px[row + x].a >= threshold) return y;
            }
            return -1;
        }

        private static int FindLastRowWithAlpha(Color32[] px, int w, int h, byte threshold, int startY)
        {
            for (int y = h - 1; y >= startY; y--)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                    if (px[row + x].a >= threshold) return y;
            }
            return startY;
        }

        private static int FindFirstColWithAlpha(Color32[] px, int w, byte threshold, int yStart, int yEnd)
        {
            for (int x = 0; x < w; x++)
            {
                for (int y = yStart; y <= yEnd; y++)
                    if (px[y * w + x].a >= threshold) return x;
            }
            return 0;
        }

        private static int FindLastColWithAlpha(Color32[] px, int w, byte threshold, int yStart, int yEnd, int startX)
        {
            for (int x = w - 1; x >= startX; x--)
            {
                for (int y = yStart; y <= yEnd; y++)
                    if (px[y * w + x].a >= threshold) return x;
            }
            return startX;
        }

        private static Texture2D ReadIntoTexture2D(RenderTexture src, int w, int h)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = src;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }
    }
}
#endif
