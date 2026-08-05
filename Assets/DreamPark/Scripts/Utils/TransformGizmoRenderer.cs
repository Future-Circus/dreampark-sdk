using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
#endif

/// <summary>
/// Draws the DreamPark TransformGizmo model as an editor-only gizmo instead of as a real
/// GameObject with a MeshRenderer.
///
/// The source model is a single mesh whose colours come from a small texture atlas, so the
/// mesh is split by sampled texture colour into one gizmo mesh per colour and each part is
/// drawn with Gizmos.DrawMesh. The result looks like the model, but nothing is instantiated:
/// no renderer, no draw call, and nothing that can leak into a build.
///
/// The model is resolved through the AssetDatabase at draw time, so this component holds no
/// serialized reference to the mesh or texture and neither asset is pulled into a player build.
/// </summary>
[AddComponentMenu("DreamPark/Transform Gizmo Renderer")]
public class TransformGizmoRenderer : MonoBehaviour
{
    public const string DefaultModelPath = "Assets/DreamPark/Models/TransformGizmo.fbx";

    [Tooltip("Uniform scale applied on top of this Transform, matching the scale the old " +
             "TransformGizmo child object used.")]
    [SerializeField] private float scale = 0.1f;

    [Tooltip("Multiplies the alpha of every colour sampled from the model's texture.")]
    [Range(0f, 1f)]
    [SerializeField] private float opacity = 1f;

    [Tooltip("Asset path of the model to draw. Leave empty to use the default TransformGizmo. " +
             "If the path is missing, the project is searched for an asset with the same file name.")]
    [SerializeField] private string modelPath = DefaultModelPath;

#if UNITY_EDITOR
    private struct Part
    {
        public Mesh Mesh;
        public Color Color;
        public Matrix4x4 LocalMatrix;
    }

    private static readonly Dictionary<string, Part[]> Cache = new Dictionary<string, Part[]>();

    private void OnDrawGizmos()
    {
        if (scale <= 0f || opacity <= 0f)
        {
            return;
        }

        Part[] parts = GetParts(string.IsNullOrEmpty(modelPath) ? DefaultModelPath : modelPath);
        if (parts == null || parts.Length == 0)
        {
            return;
        }

        Matrix4x4 oldMatrix = Gizmos.matrix;
        Color oldColor = Gizmos.color;

        Matrix4x4 root = transform.localToWorldMatrix * Matrix4x4.Scale(Vector3.one * scale);

        for (int i = 0; i < parts.Length; i++)
        {
            Mesh mesh = parts[i].Mesh;
            if (mesh == null)
            {
                // A reimport invalidated the cache mid-frame; rebuild on the next draw.
                ClearCache();
                break;
            }

            Color color = parts[i].Color;
            color.a *= opacity;

            Gizmos.matrix = root * parts[i].LocalMatrix;
            Gizmos.color = color;
            Gizmos.DrawMesh(mesh, Vector3.zero);
        }

        Gizmos.color = oldColor;
        Gizmos.matrix = oldMatrix;
    }

    private static Part[] GetParts(string path)
    {
        if (Cache.TryGetValue(path, out Part[] cached))
        {
            return cached;
        }

        Part[] built = BuildParts(path);
        Cache[path] = built;
        return built;
    }

    private static Part[] BuildParts(string path)
    {
        GameObject model = ResolveModel(path);
        if (model == null)
        {
            return System.Array.Empty<Part>();
        }

        Matrix4x4 toModelRoot = model.transform.worldToLocalMatrix;
        var parts = new List<Part>();

        foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh source = filter.sharedMesh;
            if (source == null)
            {
                continue;
            }

            Material material = null;
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                material = renderer.sharedMaterial;
            }

            Matrix4x4 local = toModelRoot * filter.transform.localToWorldMatrix;
            SplitByTexture(source, material, local, parts);
        }

        return parts.ToArray();
    }

    private static GameObject ResolveModel(string path)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (model != null)
        {
            return model;
        }

        // The model was moved or renamed — fall back to a project-wide search by file name.
        string fileName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        foreach (string guid in AssetDatabase.FindAssets(fileName + " t:GameObject"))
        {
            string candidate = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(candidate) == fileName)
            {
                return AssetDatabase.LoadAssetAtPath<GameObject>(candidate);
            }
        }

        return null;
    }

    private static void SplitByTexture(Mesh source, Material material, Matrix4x4 local, List<Part> parts)
    {
        Vector3[] vertices = source.vertices;
        Vector3[] normals = source.normals;
        Vector2[] uv = source.uv;
        if (vertices.Length == 0)
        {
            return;
        }

        Color32[] pixels = ReadTexturePixels(material, out int width, out int height);
        Color fallback = FallbackColor(material);
        bool canSample = pixels != null && uv != null && uv.Length == vertices.Length;

        var triangles = new Dictionary<uint, List<int>>();
        var colors = new Dictionary<uint, Color>();

        for (int submesh = 0; submesh < source.subMeshCount; submesh++)
        {
            int[] indices = source.GetTriangles(submesh);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                Color color = fallback;
                if (canSample)
                {
                    Vector2 center = (uv[indices[i]] + uv[indices[i + 1]] + uv[indices[i + 2]]) / 3f;
                    color = Sample(pixels, width, height, center);
                }

                uint key = ColorKey(color);
                if (!triangles.TryGetValue(key, out List<int> bucket))
                {
                    bucket = new List<int>();
                    triangles.Add(key, bucket);
                    colors.Add(key, color);
                }

                bucket.Add(indices[i]);
                bucket.Add(indices[i + 1]);
                bucket.Add(indices[i + 2]);
            }
        }

        bool hasNormals = normals != null && normals.Length == vertices.Length;

        foreach (KeyValuePair<uint, List<int>> group in triangles)
        {
            var mesh = new Mesh
            {
                name = source.name + "_gizmo_" + group.Key.ToString("x8"),
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Length > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };

            mesh.vertices = vertices;
            if (hasNormals)
            {
                mesh.normals = normals;
            }

            mesh.triangles = group.Value.ToArray();
            mesh.RecalculateBounds();

            parts.Add(new Part
            {
                Mesh = mesh,
                Color = colors[group.Key],
                LocalMatrix = local
            });
        }
    }

    /// <summary>
    /// Reads the material's main texture straight off disk. Model textures normally have
    /// Read/Write disabled, so GetPixels32 on the imported asset would fail; decoding the
    /// source PNG/JPG into a scratch texture avoids changing any import settings.
    /// </summary>
    private static Color32[] ReadTexturePixels(Material material, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (material == null || material.mainTexture == null)
        {
            return null;
        }

        string path = AssetDatabase.GetAssetPath(material.mainTexture);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
        {
            return null;
        }

        var scratch = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        Color32[] pixels = null;

        if (scratch.LoadImage(File.ReadAllBytes(path), false))
        {
            width = scratch.width;
            height = scratch.height;
            pixels = scratch.GetPixels32();
        }

        DestroyImmediate(scratch);
        return pixels;
    }

    private static Color FallbackColor(Material material)
    {
        if (material != null)
        {
            if (material.HasProperty("_BaseColor"))
            {
                return material.GetColor("_BaseColor");
            }

            if (material.HasProperty("_Color"))
            {
                return material.GetColor("_Color");
            }
        }

        return Color.white;
    }

    private static Color32 Sample(Color32[] pixels, int width, int height, Vector2 uv)
    {
        int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * width), 0, width - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(uv.y * height), 0, height - 1);
        return pixels[y * width + x];
    }

    private static uint ColorKey(Color32 color)
    {
        return ((uint)color.r << 24) | ((uint)color.g << 16) | ((uint)color.b << 8) | color.a;
    }

    private static void ClearCache()
    {
        foreach (KeyValuePair<string, Part[]> entry in Cache)
        {
            Part[] parts = entry.Value;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Mesh != null)
                {
                    DestroyImmediate(parts[i].Mesh);
                }
            }
        }

        Cache.Clear();
    }

    private class ModelChangeWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] movedTo, string[] movedFrom)
        {
            if (Cache.Count == 0)
            {
                return;
            }

            ClearCache();
        }
    }
#endif
}
