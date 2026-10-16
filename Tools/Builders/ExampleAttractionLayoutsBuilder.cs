// Builds the demonstration content inside the template's Attraction.prefab:
// a play-area rug plus a simple Base layout, two layout frames (11 × 14 ft Custom, 8 × 12 ft Minimum)
// and two wall layers (Front, Left), then bakes it. Everything it adds is named
// "EXAMPLE …" (objects) or "Example …" (frames, materials) so creators can see
// what to delete. Idempotent: every run removes the previous examples first.
//
//   unity command --project-path . run_script --file Tools/Builders/ExampleAttractionLayoutsBuilder.cs \
//       --entry ExampleAttractionLayoutsBuilder.Build --timeout 120000
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DreamPark;
using UnityEditor;
using UnityEngine;

public static class ExampleAttractionLayoutsBuilder
{
    const string ContentId = "YOUR_GAME_HERE";
    const string PrefabPath = "Assets/Content/" + ContentId + "/Prefabs/Attraction.prefab";
    const string MaterialFolder = "Assets/Content/" + ContentId + "/Materials/Examples";
    const string Prefix = "EXAMPLE ";
    const float Ft = 0.3048f;

    const string CustomFrameName = "Example 11 × 14 ft";
    const string MinimumFrameName = "Example 8 × 12 ft (Minimum)";

    public static string Build()
    {
        var mats = new Dictionary<string, Material>
        {
            ["Teal"] = EnsureMaterial("Example_Teal", new Color(0.16f, 0.66f, 0.64f)),
            ["Orange"] = EnsureMaterial("Example_Orange", new Color(0.95f, 0.55f, 0.18f)),
            ["Blue"] = EnsureMaterial("Example_Blue", new Color(0.25f, 0.42f, 0.85f)),
            ["Pink"] = EnsureMaterial("Example_Pink", new Color(0.88f, 0.33f, 0.55f)),
            ["Rug"] = EnsureMaterial("Example_Rug", new Color(0.36f, 0.38f, 0.42f)),
        };

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var attraction = root.GetComponent<AttractionTemplate>();
            if (attraction == null) throw new InvalidOperationException("No AttractionTemplate on " + PrefabPath);

            // Keep frame / wall-layer ids stable across rebuilds (ids are never derived from sizes).
            string customId = attraction.LayoutFrames.FirstOrDefault(f => f != null && f.name == CustomFrameName)?.id
                ?? "f" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string frontId = attraction.WallLayers.FirstOrDefault(w => w != null && w.side == AttractionWallSide.Front)?.id
                ?? "w" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string leftId = attraction.WallLayers.FirstOrDefault(w => w != null && w.side == AttractionWallSide.Left)?.id
                ?? "w" + Guid.NewGuid().ToString("N").Substring(0, 8);

            foreach (Transform child in root.transform.Cast<Transform>().ToList())
                if (child.name.StartsWith(Prefix, StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(child.gameObject);

            // ── Base layout: 14 × 16 ft (Micro), x = width, z = length ───────────
            // The play-area rug defines each layout's floor. It is centred, so the
            // automatic shrink cannot pull it inward: each layout shrinks to its rug,
            // and the smaller layouts are reached through the frames below instead of
            // by compacting Base. Without something like it, a sparse Base shrinks
            // below every frame and the frames are never chosen.
            var rug = Block(root, "Play Area Rug", PrimitiveType.Cube, mats["Rug"],
                new Vector3(0f, 0.01f, 0f), Rug(13f, 15f));
            var center = Block(root, "Centerpiece", PrimitiveType.Cylinder, mats["Teal"],
                new Vector3(0f, 0.5f, 0f), new Vector3(0.8f, 0.5f, 0.8f));
            var pillarNE = Block(root, "Pillar NE", PrimitiveType.Cube, mats["Orange"], new Vector3(1.6f, 0.8f, 1.9f), new Vector3(0.4f, 1.6f, 0.4f));
            var pillarNW = Block(root, "Pillar NW", PrimitiveType.Cube, mats["Orange"], new Vector3(-1.6f, 0.8f, 1.9f), new Vector3(0.4f, 1.6f, 0.4f));
            var pillarSE = Block(root, "Pillar SE", PrimitiveType.Cube, mats["Orange"], new Vector3(1.6f, 0.8f, -1.9f), new Vector3(0.4f, 1.6f, 0.4f));
            var pillarSW = Block(root, "Pillar SW", PrimitiveType.Cube, mats["Orange"], new Vector3(-1.6f, 0.8f, -1.9f), new Vector3(0.4f, 1.6f, 0.4f));
            var benchL = Block(root, "Bench Left", PrimitiveType.Cube, mats["Blue"], new Vector3(-1.15f, 0.225f, 0f), new Vector3(0.4f, 0.45f, 1.2f));
            var benchR = Block(root, "Bench Right", PrimitiveType.Cube, mats["Blue"], new Vector3(1.15f, 0.225f, 0f), new Vector3(0.4f, 0.45f, 1.2f));

            // Wall extras: off in Base ("no walls" is the baseline), on only when that side is walled.
            var shelf = Block(root, "Wall Shelf (Front wall)", PrimitiveType.Cube, mats["Pink"],
                new Vector3(0f, 1.2f, 2.25f), new Vector3(1.6f, 0.08f, 0.35f));
            var panel = Block(root, "Wall Panel (Left wall)", PrimitiveType.Cube, mats["Pink"],
                new Vector3(-2.08f, 1.0f, 0f), new Vector3(0.06f, 1.2f, 1.6f));
            shelf.gameObject.SetActive(false);
            panel.gameObject.SetActive(false);

            // ── Layout frames ────────────────────────────────────────────────────
            var custom = new AttractionLayoutFrame
            {
                id = customId,
                name = CustomFrameName,
                kind = AttractionLayoutFrameKind.Custom,
                sizeFeet = new Vector2(11f, 14f),
                overrides = new List<AttractionLayoutOverride>
                {
                    Resize(root, rug, Rug(10.5f, 13.5f)),
                    Move(root, pillarNE, new Vector3(1.25f, 0.8f, 1.6f)),
                    Move(root, pillarNW, new Vector3(-1.25f, 0.8f, 1.6f)),
                    Move(root, pillarSE, new Vector3(1.25f, 0.8f, -1.6f)),
                    Move(root, pillarSW, new Vector3(-1.25f, 0.8f, -1.6f)),
                    Move(root, benchL, new Vector3(-0.9f, 0.225f, 0f)),
                    Move(root, benchR, new Vector3(0.9f, 0.225f, 0f)),
                },
            };
            var minimum = new AttractionLayoutFrame
            {
                id = AttractionTemplate.MinimumFrameId,
                name = MinimumFrameName,
                kind = AttractionLayoutFrameKind.Minimum,
                sizeFeet = new Vector2(8f, 12f),
                overrides = new List<AttractionLayoutOverride>
                {
                    Resize(root, rug, Rug(7.5f, 11.5f)),
                    Move(root, pillarNE, new Vector3(0.85f, 0.8f, 1.4f)),
                    Move(root, pillarSW, new Vector3(-0.85f, 0.8f, -1.4f)),
                    Hide(root, pillarNW),
                    Hide(root, pillarSE),
                    Hide(root, benchL),
                    Hide(root, benchR),
                },
            };
            attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { custom, minimum });

            // ── Wall layers (list order = priority) ─────────────────────────────
            Vector2 baseFootprint = attraction.DimensionsInFeet * Ft;
            var front = new AttractionWallLayer
            {
                id = frontId,
                side = AttractionWallSide.Front,
                authoredFootprintMeters = baseFootprint,
                overrides = new List<AttractionLayoutOverride>
                {
                    Show(root, shelf),
                    // Give the shelf room: the centerpiece steps back from the front wall.
                    Move(root, center, new Vector3(0f, 0.5f, -0.4f)),
                },
            };
            var left = new AttractionWallLayer
            {
                id = leftId,
                side = AttractionWallSide.Left,
                authoredFootprintMeters = baseFootprint,
                overrides = new List<AttractionLayoutOverride>
                {
                    // Show-only. (A wall layer's root-level moves keep their distance from
                    // ITS wall at every footprint, so moving the bench here would push it into
                    // the centerpiece in the narrower frames; the bake's overlap check flags that.)
                    Show(root, panel),
                },
            };
            attraction.SetWallLayers(new List<AttractionWallLayer> { front, left });

            // Bake Base + every frame (and record Base states for the wall layers).
            var bake = Type.GetType("AttractionPackingBaker, Assembly-CSharp-Editor")
                ?.GetMethod("Bake", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new[] { typeof(AttractionTemplate), typeof(bool) }, null);
            if (bake == null) throw new InvalidOperationException("AttractionPackingBaker.Bake not found");
            bool baked = (bool)bake.Invoke(null, new object[] { attraction, false });

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);

            string Fmt(Vector2 m) => $"{m.x / Ft:0.#}×{m.y / Ft:0.#} ft";
            var lines = new List<string> { $"baked={baked}", $"base authored {Fmt(baseFootprint)}" };
            if (attraction.HasPackingBake)
                lines.Add($"base shrink {Fmt(attraction.PackingBake.ShrinkFootprintMeters)} grow {Fmt(attraction.PackingBake.GrowFootprintMeters)}");
            foreach (var f in attraction.LayoutFrames)
                lines.Add(f.Bake != null
                    ? $"{f.name}: shrink {Fmt(f.Bake.ShrinkFootprintMeters)} grow {Fmt(f.Bake.GrowFootprintMeters)}"
                    : $"{f.name}: NO BAKE");
            lines.Add("wall layers: " + string.Join(", ", attraction.WallLayers.Select(w => w.side + " " + w.id)));
            return string.Join(" | ", lines);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static Transform Block(GameObject root, string name, PrimitiveType type, Material mat, Vector3 pos, Vector3 scale)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = Prefix + name;
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go.transform;
    }

    static string PathOf(GameObject root, Transform t) => t == root.transform ? "" : t.name; // all examples are root children

    static AttractionLayoutOverride Move(GameObject root, Transform t, Vector3 pos) => new AttractionLayoutOverride
        { path = PathOf(root, t), target = t, setPosition = true, localPosition = pos };

    static Vector3 Rug(float widthFeet, float lengthFeet) => new Vector3(widthFeet * Ft, 0.02f, lengthFeet * Ft);

    static AttractionLayoutOverride Resize(GameObject root, Transform t, Vector3 scale) => new AttractionLayoutOverride
        { path = PathOf(root, t), target = t, setScale = true, localScale = scale };

    static AttractionLayoutOverride Hide(GameObject root, Transform t) => new AttractionLayoutOverride
        { path = PathOf(root, t), target = t, setActive = true, active = false };

    static AttractionLayoutOverride Show(GameObject root, Transform t) => new AttractionLayoutOverride
        { path = PathOf(root, t), target = t, setActive = true, active = true };

    static Material EnsureMaterial(string name, Color color)
    {
        if (!AssetDatabase.IsValidFolder(MaterialFolder))
        {
            string parent = "Assets/Content/" + ContentId + "/Materials";
            if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets/Content/" + ContentId, "Materials");
            AssetDatabase.CreateFolder(parent, "Examples");
        }
        string path = MaterialFolder + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            var shader = Shader.Find("Shader Graphs/DreamPark-UniversalShader");
            if (shader == null) throw new InvalidOperationException("DreamPark-UniversalShader not found");
            mat = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_baseColor", color);
        // Opaque ⇒ Alpha Clipping on (both _AlphaClip and _ALPHATEST_ON), or occlusion is discarded.
        DreamPark.EditorTools.Shaders.DreamParkMaterialRules.EnforceAndDirty(mat);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }
}
