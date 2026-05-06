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
        private const float kPaddingMultiplier = 1.3f;

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

                // Create render texture with MSAA
                RenderTexture rt = new RenderTexture(resolution, resolution, 24, RenderTextureFormat.ARGB32);
                rt.antiAliasing = 8;
                rt.Create();

                // Create temporary camera
                GameObject camObj = new GameObject("PreviewCamera") { hideFlags = HideFlags.HideAndDontSave };
                Camera cam = camObj.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.targetTexture = rt;
                cam.nearClipPlane = 0.01f;
                cam.fieldOfView = 30f;

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

                // Read pixels into Texture2D
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;

                Texture2D result = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                result.Apply();

                RenderTexture.active = previous;

                // Cleanup
                Object.DestroyImmediate(camObj);
                Object.DestroyImmediate(lightObj);
                Object.DestroyImmediate(fillLightObj);
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

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        private static void PositionCamera(Camera cam, Bounds bounds)
        {
            Vector3 center = bounds.center;
            float radius = bounds.extents.magnitude;

            // Calculate distance needed to fit the object in view with padding
            float halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float distance = (radius * kPaddingMultiplier) / Mathf.Sin(halfFov);

            // Convert spherical coordinates (elevation, azimuth) to direction
            float elevRad = kElevationAngle * Mathf.Deg2Rad;
            float azimRad = kAzimuthAngle * Mathf.Deg2Rad;

            Vector3 direction = new Vector3(
                Mathf.Cos(elevRad) * Mathf.Sin(azimRad),
                Mathf.Sin(elevRad),
                Mathf.Cos(elevRad) * Mathf.Cos(azimRad)
            ).normalized;

            cam.transform.position = center + direction * distance;
            cam.transform.LookAt(center);
            cam.farClipPlane = distance + radius * 2f;
        }
    }
}
#endif
