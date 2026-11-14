// PortalCrossersTest: the proof rig for portal OPENING crossers (Nov 2026, sdk-crossers). A run_script builder; run it IN
// PLAY MODE in your own editor (nothing is saved: the rig lives only in the play session).
//
//   Setup '["opening"|"volume", "<offset x,y,z>"]'   a stand-in room (bright walls = passthrough) with an L-shaped portal (wall
//                                                    window + floor opening, one PortalScene), a walker (SkinnedMeshRenderer,
//                                                    DreamPark-UniversalShader), box A on a rail through the wall and box B on
//                                                    a lift down through the floor (DreamPark-Unlit), a particle trail through
//                                                    the wall and sparks under box B (DreamPark/Particles), and the player-eye
//                                                    camera. "opening": AddCrosser against each crosser's own opening;
//                                                    "volume": the older PortalScene.crossers + playVolumes (the "before").
//   Record '["<outDir>", "<seconds>", "<camera>"]'   frame-perfect JPGs (Time.captureFramerate 30) of the player-eye camera
//                                                    from t = 0 of the motion; camera "eye" (default) or "side".
//   Pose '["<t>", "<camera>"]'                        freeze the motion at t seconds (-1 = run)
//   Status []                                        recorder state + PortalRenderer.Last
//   Teardown []                                      remove the rig, captureFramerate 0
//
// Motion (seconds): walker out through the wall 0..5 and back 5..10 (crossing the window plane at t = 2.62 and 7.38); box A
// period 8 (crossing 2.0 / 6.0); box B down through the floor, period 6 (crossing 1.5 / 4.5); trail emitter period 7.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DreamPark;
using UnityEngine;
using UnityEngine.Rendering;

public static class PortalCrossersTest
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    const string RootName = "PXTest_Root";

    public static string Setup(string mode, string offset)
    {
        if (!Application.isPlaying) return "[px] enter play mode first";
        Teardown();
        bool opening = mode != "volume";
        Vector3 off = ParseV(offset);
        var root = new GameObject(RootName);
        var room = new GameObject("Room").transform;
        room.SetParent(root.transform, false);

        // ── light (the walker is lit) ───────────────────────────────────────────────────────────────────────────────────
        var lg = new GameObject("Sun");
        lg.transform.SetParent(root.transform, false);
        lg.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var light = lg.AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = 1.1f; light.shadows = LightShadows.None;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);

        // ── the stand-in REAL room (bright, busy: passthrough), just behind the portal planes ────────────────────────
        Texture2D busy = BusyTexture();
        Material wallM = UrpUnlit(Color.white, busy);
        Quad(room, "Real_BackWall", new Vector3(0f, 1.5f, 3.02f), Quaternion.identity, new Vector3(7f, 3f, 1f), wallM);
        Quad(room, "Real_Floor", new Vector3(0f, -0.02f, 0.5f), Quaternion.Euler(90f, 0f, 0f), new Vector3(7f, 5.1f, 1f), UrpUnlit(new Color(0.86f, 0.8f, 0.7f), busy, 3f));
        Quad(room, "Real_RightWall", new Vector3(3.5f, 1.5f, 0.5f), Quaternion.Euler(0f, 90f, 0f), new Vector3(5.1f, 3f, 1f), UrpUnlit(new Color(0.8f, 0.9f, 1f), busy, 2f));
        Quad(room, "Real_FrontWall", new Vector3(0f, 1.5f, -2f), Quaternion.Euler(0f, 180f, 0f), new Vector3(7f, 3f, 1f), UrpUnlit(new Color(0.95f, 0.95f, 0.85f), busy, 2f));
        Quad(room, "Real_Ceiling", new Vector3(0f, 3f, 0.5f), Quaternion.Euler(-90f, 0f, 0f), new Vector3(7f, 5.1f, 1f), UrpUnlit(new Color(0.92f, 0.92f, 0.9f), null));
        Quad(room, "Real_LeftWall", new Vector3(-3.5f, 1.5f, 0.5f), Quaternion.Euler(0f, -90f, 0f), new Vector3(5.1f, 3f, 1f), UrpUnlit(new Color(1f, 0.92f, 0.85f), busy, 2f));

        // ── the far world (a PortalScene): ground beyond the wall, a canyon under the floor opening, pillars ─────────
        var sceneGo = new GameObject("CanyonScene");
        sceneGo.transform.SetParent(room, false);
        Transform S = sceneGo.transform;
        Material grass = UrpLit(new Color(0.35f, 0.62f, 0.3f)), rock = UrpLit(new Color(0.72f, 0.45f, 0.28f)), deep = UrpLit(new Color(0.45f, 0.28f, 0.2f));
        Box(S, "Ground_Right", new Vector3(12f, -0.25f, 33f), new Vector3(20f, 0.5f, 60f), grass);
        Box(S, "Ground_Left", new Vector3(-10.9f, -0.25f, 33f), new Vector3(22.2f, 0.5f, 60f), grass);
        Box(S, "Canyon_Floor", new Vector3(1.1f, -4.25f, 12f), new Vector3(1.8f, 0.5f, 22f), deep);
        Box(S, "Canyon_WallL", new Vector3(0.1f, -2f, 12f), new Vector3(0.2f, 4f, 22f), rock);
        Box(S, "Canyon_WallR", new Vector3(2.1f, -2f, 12f), new Vector3(0.2f, 4f, 22f), rock);
        Box(S, "Canyon_Back", new Vector3(1.1f, -2f, 1.5f), new Vector3(2.2f, 4f, 0.2f), rock);
        for (int i = 0; i < 8; i++)
        {
            float z = 8f + i * 6f, x = (i % 2 == 0 ? 3.5f : -5f) + (i % 3);
            Box(S, "Pillar" + i, new Vector3(x, 2f, z), new Vector3(1f, 4f + (i % 3) * 2f, 1f), i % 2 == 0 ? rock : UrpLit(new Color(0.3f, 0.45f, 0.75f)));
        }
        var sc = sceneGo.AddComponent<PortalScene>();
        sc.source = room;
        sc.offset = off;
        sc.emptyIsPassthrough = false;
        sc.background = new Color(0.55f, 0.75f, 0.98f, 1f);

        // ── the L-shaped portal: a wall window and a floor opening, one scene ───────────────────────────────────────
        Material pm = new Material(Shader.Find("DreamPark/Portal"));
        PortalSurface wall = Surface(room, "Surface_Wall", new Vector3(0f, 1.1f, 3f), Quaternion.identity, new Vector3(4f, 2.2f, 1f), pm, sc);
        PortalSurface floor = Surface(room, "Surface_Floor", new Vector3(1.1f, 0f, 2.3f), Quaternion.Euler(90f, 0f, 0f), new Vector3(1.8f, 1.4f, 1f), pm, sc);

        // ── crossers ─────────────────────────────────────────────────────────────────────────────────────────────────
        Transform walker = Walker(room);
        Material orange = SdkUnlit(new Color(1f, 0.55f, 0.1f)), cyan = SdkUnlit(new Color(0.1f, 0.85f, 1f));
        Transform boxA = Box(room, "BoxA_Rail", new Vector3(-0.3f, 1.2f, 1f), new Vector3(0.5f, 0.5f, 0.7f), orange).transform;
        Transform boxB = Box(room, "BoxB_Lift", new Vector3(1.4f, 1f, 2.3f), new Vector3(0.6f, 0.6f, 0.6f), cyan).transform;
        Box(room, "RailA", new Vector3(-0.3f, 0.92f, 1.6f), new Vector3(0.05f, 0.05f, 2.8f), UrpUnlit(new Color(0.25f, 0.25f, 0.28f), null));
        Transform trail = Trail(room, "Trail", new Color(1f, 0.35f, 0.9f), 0.12f, 40f);
        Transform sparks = Trail(boxB, "Sparks", new Color(1f, 0.9f, 0.2f), 0.08f, 30f);
        sparks.localPosition = new Vector3(0f, -0.55f, 0f);
        if (opening)
        {
            sc.AddCrosser(walker, wall);
            sc.AddCrosser(boxA, wall);
            sc.AddCrosser(trail, wall);
            sc.AddCrosser(boxB, floor);
        }
        else
        {
            var vol = new GameObject("PlayVolume").transform;
            vol.SetParent(room, false);
            vol.localPosition = new Vector3(0f, 1.45f, 0.5f);
            vol.localScale = new Vector3(7f, 2.9f, 5f);       // the room: x -3.5..3.5, y 0..2.9, z -2..3
            sc.playVolumes.Add(vol);
            sc.crossers.Add(walker); sc.crossers.Add(boxA); sc.crossers.Add(boxB); sc.crossers.Add(trail);
        }
        sc.Refresh();

        // ── driver + player-eye camera ───────────────────────────────────────────────────────────────────────────────
        var drv = root.AddComponent<PXDriver>();
        drv.walker = walker; drv.boxA = boxA; drv.boxB = boxB; drv.trail = trail;
        drv.legs = new[] { walker.Find("Hips/LegL"), walker.Find("Hips/LegR") };
        drv.arms = new[] { walker.Find("Hips/Spine/ArmL"), walker.Find("Hips/Spine/ArmR") };
        var camGo = new GameObject("PlayerEye");
        camGo.transform.SetParent(root.transform, false);
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 70f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 100f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.2f, 0.2f, 0.22f);
        drv.cam = cam;
        drv.SetCamera("eye");
        var rt = new RenderTexture(1280, 720, 24) { antiAliasing = 4, name = "PXEye" };
        cam.targetTexture = rt;
        var rec = root.AddComponent<PXRec>();
        rec.cam = cam;
        return "[px] setup " + (opening ? "OPENING crossers" : "VOLUME crossers (before)") + ", offset " + off + ", surfaces 2, crossers 4";
    }

    // (each run_script call is a new in-memory assembly, so the live rig's components are reached by SendMessage)
    public static string Record(string outDir, string seconds, string camera) { return Rec(outDir, seconds, camera, ""); }

    /// <summary>A few frames of the pose set by Pose.</summary>
    public static string Still(string outDir, string camera) { return Rec(outDir, "0.2", camera, "hold"); }

    static string Rec(string outDir, string seconds, string camera, string hold)
    {
        var root = GameObject.Find(RootName);
        if (root == null) return "[px] no rig: Setup first";
        Directory.CreateDirectory(outDir);
        foreach (string f in Directory.GetFiles(outDir, "f_*.jpg")) File.Delete(f);
        root.SendMessage("PxCmd", "record|" + outDir + "|" + seconds + "|" + (string.IsNullOrEmpty(camera) ? "eye" : camera) + "|" + hold);
        return "[px] recording " + seconds + " s to " + outDir + " (progress: " + outDir + "/_status.txt)";
    }

    public static string Pose(string t, string camera)
    {
        var root = GameObject.Find(RootName);
        if (root == null) return "[px] no rig";
        root.SendMessage("PxCmd", "pose|" + t + "|" + (string.IsNullOrEmpty(camera) ? "eye" : camera));
        return "[px] pose t=" + t;
    }

    public static string Status()
    {
        var root = GameObject.Find(RootName);
        if (root == null) return "[px] no rig";
        var L = PortalRenderer.Last;
        string straddle = "";
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.HasPropertyBlock()) continue;
            var b = new MaterialPropertyBlock(); r.GetPropertyBlock(b);
            Vector4 c = b.GetVector("_DPCross");
            if (c != Vector4.zero) straddle += r.name + "(" + c.x + "," + c.y + ") ";
        }
        string hidden = "";
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) if (r.forceRenderingOff && r.gameObject.layer != 16) hidden += r.name + " ";
        return "[px] portal renders " + L.renders + " cams " + L.cameras + " cpu " + L.cpuMs.ToString("0.00", CI) + " ms | clip blocks: " + straddle
            + "| hidden from room: " + hidden + "| frame " + Time.frameCount;
    }

    /// <summary>Debug: "portal 0/1" switches PortalRenderer off/on (the room render alone).</summary>
    public static string Debug(string what, string on)
    {
        if (what == "portal") PortalRenderer.Enabled = on == "1";
        return "[px] " + what + "=" + on;
    }

    public static string Teardown()
    {
        Time.captureFramerate = 0;
        var root = GameObject.Find(RootName);
        if (root == null) return "[px] nothing to remove";
        UnityEngine.Object.Destroy(root);
        return "[px] removed";
    }

    // ── pieces ──────────────────────────────────────────────────────────────────────────────────────────────────────
    static Vector3 ParseV(string s)
    {
        if (string.IsNullOrEmpty(s)) return Vector3.zero;
        string[] p = s.Split(',');
        return new Vector3(float.Parse(p[0], CI), float.Parse(p[1], CI), float.Parse(p[2], CI));
    }

    static GameObject Quad(Transform parent, string name, Vector3 pos, Quaternion rot, Vector3 scale, Material m)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        UnityEngine.Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos; go.transform.localRotation = rot; go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = m;
        return go;
    }

    static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material m)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        UnityEngine.Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos; go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = m;
        return go;
    }

    // a Unity quad faces -Z: rotate it so its front faces the room (the wall: -Z; the floor: +Y)
    static PortalSurface Surface(Transform parent, string name, Vector3 pos, Quaternion rot, Vector3 scale, Material m, PortalScene sc)
    {
        var go = Quad(parent, name, pos, rot, scale, m);
        var s = go.AddComponent<PortalSurface>();
        s.scene = sc;
        return s;
    }

    static Material UrpUnlit(Color c, Texture2D tex, float tiling = 1f)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        m.SetColor("_BaseColor", c);
        if (tex != null) { m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", new Vector2(tiling * 2f, tiling)); }
        return m;
    }

    static Material UrpLit(Color c)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", 0.1f);
        return m;
    }

    static Material SdkUnlit(Color c)
    {
        var m = new Material(Shader.Find("Shader Graphs/DreamPark-Unlit"));
        m.SetColor("_baseColor", c);
        return m;
    }

    static Material SdkLit(Color c)
    {
        var m = new Material(Shader.Find("Shader Graphs/DreamPark-UniversalShader"));
        m.SetColor("_baseColor", c);
        m.SetFloat("_smoothness", 0.25f);
        m.EnableKeyword("_ALPHATEST_ON");
        m.SetFloat("_AlphaClip", 1f);
        return m;
    }

    // posters, stripes and dots: a busy, bright "real room" so the clip edge reads against passthrough, not the dark sky
    static Texture2D BusyTexture()
    {
        var t = new Texture2D(256, 128, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
        var px = new Color32[256 * 128];
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 256; x++)
            {
                float v = 0.82f + 0.12f * (((x / 16) + (y / 16)) % 2);
                Color c = new Color(v, v * 0.97f, v * 0.92f);
                if ((x / 32) % 3 == 0 && y > 60 && y < 110) c = Color.Lerp(c, new Color(0.9f, 0.3f, 0.3f), 0.55f);
                if ((x / 32) % 3 == 1 && y > 20 && y < 70) c = Color.Lerp(c, new Color(0.25f, 0.45f, 0.95f), 0.5f);
                if (y % 32 < 2) c *= 0.7f;
                px[y * 256 + x] = c;
            }
        t.SetPixels32(px); t.Apply(true);
        return t;
    }

    static Transform Trail(Transform parent, string name, Color col, float size, float rate)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0.6f, 1.3f, 0.8f);
        var ball = Box(go.transform, "Emitter", Vector3.zero, Vector3.one * 0.1f, SdkUnlit(col));
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 1.6f; main.startSpeed = 0.05f; main.startSize = size; main.startColor = col;
        main.maxParticles = 400;
        var em = ps.emission; em.rateOverTime = 0f; em.rateOverDistance = rate;
        var shape = ps.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.03f;
        var sol = ps.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.2f));
        var m = new Material(Shader.Find("DreamPark/Particles"));
        m.SetColor("_BaseColor", Color.white);
        m.EnableKeyword("_BLENDMODE_ADDITIVE"); m.EnableKeyword("_VC_MULTIPLY");
        m.SetFloat("_SrcBlend", 5f); m.SetFloat("_DstBlend", 1f); m.SetFloat("_ZWrite", 0f);
        m.renderQueue = 3000;
        go.GetComponent<ParticleSystemRenderer>().sharedMaterial = m;
        ps.Play();
        return go.transform;
    }

    // a rigid-skinned humanoid: one SkinnedMeshRenderer, bones Hips / Spine / Head / LegL / LegR / ArmL / ArmR
    static Transform Walker(Transform parent)
    {
        var root = new GameObject("Walker").transform;
        root.SetParent(parent, false);
        root.localPosition = new Vector3(1.0f, 0f, 0.8f);
        Transform hips = Bone("Hips", root, new Vector3(0f, 0.9f, 0f));
        Transform spine = Bone("Spine", hips, new Vector3(0f, 0.05f, 0f));
        Transform head = Bone("Head", spine, new Vector3(0f, 0.6f, 0f));
        Transform legL = Bone("LegL", hips, new Vector3(-0.12f, 0f, 0f));
        Transform legR = Bone("LegR", hips, new Vector3(0.12f, 0f, 0f));
        Transform armL = Bone("ArmL", spine, new Vector3(-0.3f, 0.5f, 0f));
        Transform armR = Bone("ArmR", spine, new Vector3(0.3f, 0.5f, 0f));
        var bones = new[] { hips, spine, head, legL, legR, armL, armR };
        var v = new List<Vector3>(); var n = new List<Vector3>(); var tr = new List<int>(); var bw = new List<BoneWeight>();
        // (positions in the walker root's space at bind)
        AddBox(v, n, tr, bw, new Vector3(0f, 1.25f, 0f), new Vector3(0.46f, 0.62f, 0.26f), 1);       // torso
        AddBox(v, n, tr, bw, new Vector3(0f, 1.66f, 0.02f), new Vector3(0.26f, 0.26f, 0.26f), 2);    // head
        AddBox(v, n, tr, bw, new Vector3(-0.12f, 0.46f, 0f), new Vector3(0.17f, 0.9f, 0.18f), 3);    // legs
        AddBox(v, n, tr, bw, new Vector3(0.12f, 0.46f, 0f), new Vector3(0.17f, 0.9f, 0.18f), 4);
        AddBox(v, n, tr, bw, new Vector3(-0.3f, 1.15f, 0f), new Vector3(0.12f, 0.66f, 0.13f), 5);    // arms
        AddBox(v, n, tr, bw, new Vector3(0.3f, 1.15f, 0f), new Vector3(0.12f, 0.66f, 0.13f), 6);
        AddBox(v, n, tr, bw, new Vector3(0f, 1.68f, 0.15f), new Vector3(0.18f, 0.06f, 0.04f), 2);    // face stripe
        var mesh = new Mesh { name = "WalkerMesh" };
        mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetTriangles(tr, 0);
        mesh.boneWeights = bw.ToArray();
        var bind = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++) bind[i] = bones[i].worldToLocalMatrix * root.localToWorldMatrix;
        mesh.bindposes = bind;
        mesh.RecalculateBounds();
        var go = new GameObject("Body");
        go.transform.SetParent(root, false);
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh; smr.bones = bones; smr.rootBone = hips;
        smr.sharedMaterial = SdkLit(new Color(0.45f, 0.75f, 0.4f));
        smr.localBounds = new Bounds(new Vector3(0f, -0.05f, 0f), new Vector3(0.9f, 1.9f, 0.7f));
        return root;
    }

    static Transform Bone(string name, Transform parent, Vector3 local)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = local;
        return t;
    }

    static void AddBox(List<Vector3> v, List<Vector3> n, List<int> tr, List<BoneWeight> bw, Vector3 c, Vector3 s, int bone)
    {
        Vector3 h = s * 0.5f;
        Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        foreach (Vector3 d in dirs)
        {
            Vector3 u = Mathf.Abs(d.y) > 0.5f ? Vector3.forward : Vector3.up;
            Vector3 r = Vector3.Cross(u, d);
            int b0 = v.Count;
            for (int i = 0; i < 4; i++)
            {
                float a = (i == 0 || i == 3) ? -1f : 1f, bb = (i < 2) ? -1f : 1f;
                Vector3 p = d + r * a + u * bb;
                v.Add(c + Vector3.Scale(p, h)); n.Add(d);
                bw.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1f });
            }
            tr.Add(b0); tr.Add(b0 + 2); tr.Add(b0 + 1); tr.Add(b0); tr.Add(b0 + 3); tr.Add(b0 + 2);
        }
    }
}

// the crossers' motion, a pure function of time (frame-exact with captureFramerate)
public class PXDriver : MonoBehaviour
{
    public Transform walker, boxA, boxB, trail;
    public Transform[] legs, arms;
    public Camera cam;
    public float fixedT = -1f;
    public int frame0;

    public void SetCamera(string which)
    {
        if (cam == null) return;
        Vector3 p, at;
        if (which == "floor") { p = new Vector3(-0.8f, 0.6f, 2.0f); at = new Vector3(1.4f, 0.0f, 2.3f); }        // crouched beside the floor opening
        else if (which == "wide") { p = new Vector3(0.3f, 1.6f, -1.6f); at = new Vector3(0f, 0.9f, 3f); }       // the whole L
        else { p = new Vector3(2.5f, 1.6f, 2.0f); at = new Vector3(-1.3f, 1.0f, 3.2f); }                         // eye: beside the wall, looking along it
        cam.transform.SetPositionAndRotation(p, Quaternion.LookRotation(at - p));
    }

    public void PxCmd(string cmd)
    {
        string[] a = cmd.Split('|');
        var CI = System.Globalization.CultureInfo.InvariantCulture;
        if (a[0] == "record")
        {
            SetCamera(a[3]);
            Time.captureFramerate = 30;
            if (a.Length < 5 || a[4] != "hold") fixedT = -1f;
            frame0 = Time.frameCount + 1;
            GetComponent<PXRec>().Begin(a[1], Mathf.RoundToInt(float.Parse(a[2], CI) * 30f), frame0);
        }
        else if (a[0] == "pose") { fixedT = float.Parse(a[1], CI); SetCamera(a[2]); }
    }

    static float Tri(float t, float period) { float p = Mathf.Repeat(t / period, 1f); return p < 0.5f ? p * 2f : 2f - p * 2f; }

    void Update()
    {
        float t = fixedT >= 0f ? fixedT : (Time.frameCount - frame0) / 30f;
        if (t < 0f) t = 0f;
        // walker: z 0.8 -> 5.0 -> 0.8 over 10 s, facing the way it walks, legs / arms swinging
        float wp = Tri(t, 10f);
        bool outward = Mathf.Repeat(t / 10f, 1f) < 0.5f;
        walker.localPosition = new Vector3(-1.5f, 0f, 0.8f + 4.2f * wp);
        walker.localRotation = Quaternion.Euler(0f, outward ? 0f : 180f, 0f);
        float sw = Mathf.Sin(t * 2f * Mathf.PI * 1.1f) * 32f;
        legs[0].localRotation = Quaternion.Euler(sw, 0f, 0f); legs[1].localRotation = Quaternion.Euler(-sw, 0f, 0f);
        arms[0].localRotation = Quaternion.Euler(-sw * 0.8f, 0f, 0f); arms[1].localRotation = Quaternion.Euler(sw * 0.8f, 0f, 0f);
        // box A: z 0.3 -> 5.3 on the rail, period 8
        boxA.localPosition = new Vector3(-0.3f, 1.2f, 0.3f + 5f * Tri(t, 8f));
        // box B: the lift, y 1.0 -> -2.0 through the floor opening, period 6
        boxB.localPosition = new Vector3(1.4f, 1.0f - 3f * Tri(t, 6f), 2.3f);
        boxB.localRotation = Quaternion.Euler(0f, t * 40f, 0f);
        // trail: out through the window and back in, weaving, period 7
        float k = Tri(t, 7f);
        trail.localPosition = new Vector3(0.4f + 0.4f * Mathf.Sin(t * 3.1f), 1.5f + 0.35f * Mathf.Sin(t * 4.3f), 1.0f + 5f * k);
    }
}

// writes the player-eye camera's frames. The camera stays enabled (so PortalRenderer renders its portal views in
// onBeforeRender, as for a headset eye), and each frame it is rendered AGAIN at the end of the frame for the capture: the
// Editor renders an off-screen camera before onBeforeRender, which would composite last frame's portal image with this
// frame's room (a one-frame lag a device never has: there every camera renders after onBeforeRender).
public class PXRec : MonoBehaviour
{
    public Camera cam;
    public int total, written, startFrame;
    public bool done = true;
    string _dir;
    Texture2D _tex;

    public void Begin(string dir, int frames, int start)
    {
        _dir = dir; total = frames; written = 0; startFrame = start; done = false;
        StopAllCoroutines();
        StartCoroutine(Loop());
    }

    System.Collections.IEnumerator Loop()
    {
        var eof = new WaitForEndOfFrame();
        while (!done)
        {
            yield return eof;
            if (Time.frameCount < startFrame) continue;
            RenderTexture rt = cam.targetTexture;
            cam.Render();
            if (_tex == null) _tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = rt;
            _tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            _tex.Apply(false);
            RenderTexture.active = was;
            File.WriteAllBytes(Path.Combine(_dir, "f_" + written.ToString("00000") + ".jpg"), _tex.EncodeToJPG(92));
            written++;
            if (written >= total) { done = true; Time.captureFramerate = 0; }
            if (done || written % 15 == 0) File.WriteAllText(Path.Combine(_dir, "_status.txt"), written + "/" + total + (done ? " DONE" : ""));
        }
    }
}
