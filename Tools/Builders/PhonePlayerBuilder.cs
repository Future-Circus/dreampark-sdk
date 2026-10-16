// Builds the template's default phone rig: Assets/Content/YOUR_GAME_HERE/Prefabs/Phone Player.prefab
// (+ its materials, ripple texture and the "Tap Ripple" particle child). Mirrors Player.prefab's
// conventions: PlayerRig root, Head (HeadTracker) / Body (BodyTracker) / Feet (FeetTracker), no hand
// trackers (phones have no hands), a LuaBehaviour child running Scripts/phone-player.lua.txt, and a
// RemoteRig child — what headset players see of a phone user: a phone slab that rides the phone
// user's head pose (on a phone the "head" IS the phone).
//
// The Phone Player SLOT stays empty (phones are spectators until a creator opts in through
// Content Uploader → Packages → "Use template Phone Player"). This builder never touches the slot.
//
// Idempotent: every run rebuilds the managed children and re-saves over the same path, so the
// prefab GUID never changes.
//
//   unity command --project-path . run_script --file Tools/Builders/PhonePlayerBuilder.cs \
//       --entry PhonePlayerBuilder.Build --timeout 120000
using System;
using System.IO;
using System.Linq;
using DreamPark;
using UnityEditor;
using UnityEngine;

public static class PhonePlayerBuilder
{
    const string ContentId = "YOUR_GAME_HERE";
    const string ContentRoot = "Assets/Content/" + ContentId;
    const string PrefabPath = ContentRoot + "/Prefabs/Phone Player.prefab";
    const string ScriptPath = ContentRoot + "/Scripts/phone-player.lua.txt";
    const string MaterialFolder = ContentRoot + "/Materials/Phone Player";
    const string RippleTexturePath = MaterialFolder + "/Phone Player Tap Ripple.png";

    static readonly string[] Managed = { "Head", "Body", "Feet", "Phone Controls", "Tap Ripple", "RemoteRig" };

    public static string Build()
    {
        var script = AssetDatabase.LoadAssetAtPath<TextAsset>(ScriptPath);
        if (script == null) throw new InvalidOperationException("Missing " + ScriptPath);

        EnsureFolder(ContentRoot, "Materials");
        EnsureFolder(ContentRoot + "/Materials", "Phone Player");
        Material body = EnsureLitMaterial("Phone Player Body", new Color(0.12f, 0.13f, 0.15f));
        Material screen = EnsureLitMaterial("Phone Player Screen", new Color(0.30f, 0.62f, 0.95f));
        Material ripple = EnsureRippleMaterial();

        bool exists = File.Exists(PrefabPath);
        GameObject root = exists ? PrefabUtility.LoadPrefabContents(PrefabPath) : new GameObject("Phone Player");
        try
        {
            foreach (Transform child in root.transform.Cast<Transform>().ToList())
                if (Managed.Contains(child.name)) UnityEngine.Object.DestroyImmediate(child.gameObject);

            var rig = root.GetComponent<PlayerRig>() ?? root.AddComponent<PlayerRig>();
            rig.gameId = ContentId;   // ContentProcessor stamps this too (any `gameId` field)

            // ── Trackers (same as Player.prefab, minus hands) ──────────────
            Child(root, "Head").AddComponent<HeadTracker>();
            Child(root, "Body").AddComponent<BodyTracker>();
            Child(root, "Feet").AddComponent<FeetTracker>();

            // ── Tap feedback: authored, stopped; Lua moves it and plays it ──
            var rippleGo = Child(root, "Tap Ripple");
            var ps = BuildRipple(rippleGo, ripple);

            // ── Controls script (a child, like Player.prefab's DamageEffect) ──
            var controls = Child(root, "Phone Controls");
            var lb = controls.AddComponent<LuaBehaviour>();
            lb.luaScript = script;
            lb.componentInjections = new[] { new ComponentInjection { name = "ripple", value = ps } };

            // ── RemoteRig: what headset players see of this phone user ──────
            // "Head" follows the remote player's head pose (RemoteRig.cs); for a
            // phone user that pose is the phone, so the slab sits at its origin.
            var remote = Child(root, "RemoteRig");
            var remoteHead = Child(remote, "Head");
            Slab(remoteHead, "Phone", body, new Vector3(0.075f, 0.155f, 0.009f), Vector3.zero);
            // The screen faces the phone user (behind the camera, -Z).
            Slab(remoteHead, "Screen", screen, new Vector3(0.067f, 0.142f, 0.002f), new Vector3(0f, 0f, -0.0050f));
            Slab(remoteHead, "Camera Bump", body, new Vector3(0.022f, 0.022f, 0.004f), new Vector3(-0.022f, 0.06f, 0.0065f));

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            if (exists) PrefabUtility.UnloadPrefabContents(root);
            else UnityEngine.Object.DestroyImmediate(root);
        }
        AssetDatabase.SaveAssets();
        return "[phone-player] built " + PrefabPath + " (slot untouched: " + DreamPark.Editor.PhonePlayerStore.AssetPath(ContentId) + ")";
    }

    static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    static void Slab(GameObject parent, string name, Material mat, Vector3 size, Vector3 localPos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        // Visual only: never something a local tap raycast or a projectile
        // should collide with by accident. Add a hitbox deliberately if wanted.
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    static ParticleSystem BuildRipple(GameObject go, Material mat)
    {
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.5f;
        main.startLifetime = 0.45f;
        main.startSpeed = 0f;
        main.startSize = 0.06f;
        main.startColor = new Color(0.55f, 0.85f, 1f, 0.9f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 8;
        main.gravityModifier = 0f;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });

        var shape = ps.shape;
        shape.enabled = false;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 4f));

        var color = ps.colorOverLifetime;
        color.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        color.color = grad;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sharedMaterial = mat;
        return ps;
    }

    static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
    }

    static Material EnsureLitMaterial(string name, Color color)
    {
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
        return mat;
    }

    static Material EnsureRippleMaterial()
    {
        Texture2D tex = EnsureRippleTexture();
        string path = MaterialFolder + "/Phone Player Tap Ripple.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            var shader = Shader.Find("DreamPark/Particles");
            if (shader == null) throw new InvalidOperationException("DreamPark/Particles not found");
            mat = new Material(shader) { name = "Phone Player Tap Ripple" };
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);
        // Alpha blend, vertex colour multiplied (the shader's defaults, made explicit).
        mat.SetFloat("_BlendMode", 0f);
        foreach (string k in new[] { "_BLENDMODE_ADDITIVE", "_BLENDMODE_PREMULTIPLIED", "_BLENDMODE_MULTIPLY" }) mat.DisableKeyword(k);
        mat.EnableKeyword("_BLENDMODE_ALPHA");
        mat.SetFloat("_VC", 1f);
        mat.DisableKeyword("_VC_OFF");
        mat.DisableKeyword("_VC_ADD");
        mat.EnableKeyword("_VC_MULTIPLY");
        mat.renderQueue = 3000;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // A soft white ring, generated so the content folder depends on nothing outside itself.
    static Texture2D EnsureRippleTexture()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.78f) / 0.16f);
                float a = Mathf.SmoothStep(0f, 1f, ring);
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply();
        byte[] png = tex.EncodeToPNG();
        UnityEngine.Object.DestroyImmediate(tex);

        string full = Path.GetFullPath(RippleTexturePath);
        if (!File.Exists(full) || !File.ReadAllBytes(full).SequenceEqual(png))
        {
            File.WriteAllBytes(full, png);
            AssetDatabase.ImportAsset(RippleTexturePath, ImportAssetOptions.ForceUpdate);
        }
        var importer = (TextureImporter)AssetImporter.GetAtPath(RippleTexturePath);
        if (importer != null && (!importer.alphaIsTransparency || importer.wrapMode != TextureWrapMode.Clamp || importer.mipmapEnabled))
        {
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(RippleTexturePath);
    }
}
