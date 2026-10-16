#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    // Hand-held props that will sit wrong in a guest's hand on the headset.
    //
    // WHY THIS WARNS
    //
    // The hand nodes (HandTracker, RemoteRig LeftHand/RightHand/Hand) follow the OpenXR
    // WRIST joint: +Z fingers, +Y back of the hand, thumb −X on the right / +X on the left.
    // A gun hung under a hand with an identity rotation therefore points its top out of the
    // back of the hand and its handle out of the palm — and looks right in older Editor
    // simulations. Birthday Bash, LaserTag and Zombiez each shipped that bug and fixed it by
    // hand. HANDHELD.md is the convention. The rules are HandheldKit's ValidateRig / Validate,
    // ported to HandheldMath.ValidateRig / ValidateProp (one definition, shared with tests):
    //   • ValidateRig FAIL -> Warning: a grip more than 10 deg / 2 cm off the device pose for its
    //     side and frame (flipVisual included) unless a script mounts it at runtime with dp.hold
    //     (or handheld_mount.lua); a missing Grip; a negative scale; a remote copy that differs
    //     from its local copy by > 5 mm / 2 deg; a prop hung on the old template pivot.
    //   • ValidateRig WARN -> Info: a prop on an active-hand node that does not re-mount.
    //   • Validate (each hand-held prop) FAIL -> Warning, WARN -> Info: markers, scale, size,
    //     Muzzle at the tip, Grip behind the Muzzle, upside-down barrel, PalmGrip mirroring.
    //
    // Warning, never Blocking: the pose is fixable in seconds (dp.hold, or
    // HandheldMath.Place in a builder) and some rigs mount from their own Lua.
    public sealed class HandheldConventionCheck : IPreUploadCheck
    {
        public const string CheckId = "handheld-convention";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Hand-held props"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "Hands follow the device wrist frame (fingers +Z, back of hand +Y, thumb −X right / +X left). "
                     + "A prop mounted any other way is rolled 90° on the headset. Mount with dp.hold() or "
                     + "HandheldMath.Mount; see Assets/DreamPark/Docs/HANDHELD.md.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();
            int i = 0;
            foreach (var root in ctx.roots)
            {
                ctx.Progress((float)i / Mathf.Max(1, ctx.roots.Count), $"Checking hand-held props in {root.name}…");
                i++;
                List<HandheldMath.Issue> issues;
                try
                {
                    issues = Scan(root.assetPath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Could not scan {root.assetPath} for hand-held props: {e.Message}");
                    continue;
                }

                foreach (var issue in issues)
                {
                    if (issue.level == HandheldMath.IssueLevel.Info) continue;
                    string path = root.assetPath;
                    string hierarchy = issue.path;
                    var finding = new Finding
                    {
                        checkId = CheckId,
                        severity = issue.level == HandheldMath.IssueLevel.Fail ? CheckSeverity.Warning : CheckSeverity.Info,
                        assetGuid = root.guid,
                        assetPath = path,
                        subKey = hierarchy + "|" + Fnv(issue.message),
                        title = $"{root.KindLabel} '{root.name}': {hierarchy}",
                        detail = hierarchy + " " + issue.message,
                    };
                    finding.fixes.Add(FixAction.Navigate("Open in Prefab Mode", () => Select(path, hierarchy)));
                    findings.Add(finding);
                }
            }
            return CheckResult.From(CheckId, findings);
        }

        // Stable across sessions (string.GetHashCode is not guaranteed to be): ignore keys persist.
        static string Fnv(string s)
        {
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return h.ToString("x8");
        }

        // HandheldKit.ValidateRig on every content root (props in a hand, local and remote, incl.
        // the template-pivot trap and remote != local), plus HandheldKit.Validate on each such prop
        // and on a content root that is itself a hand-held prop (Grip/PalmGrip/Muzzle children).
        static List<HandheldMath.Issue> Scan(string prefabPath)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return new List<HandheldMath.Issue>();
            try
            {
                var scripts = new List<string>();
                foreach (var lb in root.GetComponentsInChildren<LuaBehaviour>(true))
                {
                    var src = lb != null && lb.luaScript != null ? lb.luaScript.text : null;
                    if (!string.IsNullOrEmpty(src)) scripts.Add(src);
                }
                bool Remounts(Transform prop)
                {
                    foreach (var lb in prop.GetComponentsInChildren<LuaBehaviour>(true))
                    {
                        if (lb == null || lb.luaScript == null) continue;
                        if (lb.luaScript.name.Contains("handheld_mount")) return true;
                        if (lb.luaScript.text.IndexOf("dp.hold", StringComparison.Ordinal) >= 0) return true;
                    }
                    // a rig/manager script that holds this prop by name
                    foreach (var src in scripts)
                        if (src.IndexOf("dp.hold", StringComparison.Ordinal) >= 0 && src.IndexOf(prop.name, StringComparison.Ordinal) >= 0) return true;
                    return false;
                }

                var issues = HandheldMath.ValidateRig(root, Remounts);
                var props = new List<Transform>();
                if (HandheldMath.IsProp(root.transform)) props.Add(root.transform);
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t != root.transform && HandheldMath.IsProp(t) && HandheldMath.FindHandNode(t, out _, out _) != null) props.Add(t);
                foreach (var prop in props)
                {
                    // Props that shoot carry a Muzzle; everything else (swords, phones, balls) need not.
                    bool shoots = HandheldMath.FindMuzzle(prop) != null;
                    foreach (var issue in HandheldMath.ValidateProp(prop.gameObject, needMuzzle: shoots))
                    {
                        var i2 = issue;
                        i2.path = HandheldMath.PathOf(prop, root.transform);
                        issues.Add(i2);
                    }
                }
                return issues;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static void Select(string prefabPath, string hierarchy)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(prefabPath);
            if (stage == null) return;
            var root = stage.prefabContentsRoot.transform;
            string rel = hierarchy.StartsWith(root.name + "/") ? hierarchy.Substring(root.name.Length + 1) : hierarchy;
            var t = rel == root.name ? root : root.Find(rel);
            if (t != null) { Selection.activeGameObject = t.gameObject; EditorGUIUtility.PingObject(t.gameObject); }
        }
    }
}
#endif
