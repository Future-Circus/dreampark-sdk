#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DreamPark
{
    /// <summary>
    /// No modal dialog on any path an automated publish or preflight can reach (owner decision,
    /// 2026-10-09: "The preflight scene check shouldn't be able to raise a save dialog during an
    /// automated upload").
    ///
    /// WHY. On release night (Oct 8 2026) `dreampark_content_publish` hung the ArcadeLand Editor's
    /// main thread behind a modal twice, with nobody at the screen to click it:
    ///   1. the strict preflight's saved-state step called EditorSceneManager.SaveOpenScenes() with an
    ///      Untitled scene open. That opens a Save-As panel. It was cancelled by hand and the job failed
    ///      with "Unity could not save every open scene".
    ///   2. after "Switching to platform AndroidPlayer", AddressableAssetSettings.BuildPlayerContent ran
    ///      Addressables' BuildUtility.CheckModifiedScenesAndAskToSave(), which shows "Unsaved Scenes /
    ///      Modified Scenes must be saved to continue" whenever ANY open scene is dirty (a scene that
    ///      dirties itself on load or reimport is enough).
    /// The scene-overrides check also opened content test scenes that dirty themselves on load and
    /// then called RestoreSceneManagerSetup.
    ///
    /// Everything here is package-free and outside the !DREAMPARKCORE guard on purpose, so the
    /// EditMode suite AutomationNoModalTests runs in core as well as in the SDK.
    /// </summary>
    public static class DreamParkAutomation
    {
        /// <summary>Set to 1/true in a CI or agent shell to force automation mode for every command.</summary>
        public const string EnvironmentVariable = "DREAMPARK_AUTOMATION";

        private static int depth;

        /// <summary>
        /// True while an automated release command runs (a <see cref="Begin"/> scope is open), in
        /// batch mode, or when <see cref="EnvironmentVariable"/> is set. Interactive paths ask
        /// this before they show a dialog, and report into the command's message instead.
        /// </summary>
        public static bool IsActive => depth > 0 || Application.isBatchMode || EnvironmentFlag();

        /// <summary>Marks the calling command as automation until the returned scope is disposed. Nests.</summary>
        public static IDisposable Begin()
        {
            depth++;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (depth > 0) depth--;
            }
        }

        private static bool EnvironmentFlag()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ scenes

        public static bool IsUntitled(Scene scene) => string.IsNullOrEmpty(scene.path);

        public static string Label(Scene scene) => IsUntitled(scene)
            ? (string.IsNullOrEmpty(scene.name) ? "Untitled scene" : "Untitled scene '" + scene.name + "'")
            : scene.path;

        /// <summary>True when <paramref name="assetPath"/> lies inside <paramref name="folder"/>.</summary>
        public static bool IsUnder(string assetPath, string folder)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(folder)) return false;
            string p = assetPath.Replace('\\', '/');
            string f = folder.Replace('\\', '/').TrimEnd('/') + "/";
            return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
        }

        public static List<Scene> LoadedDirtyScenes()
        {
            var result = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty) result.Add(scene);
            }
            return result;
        }

        public sealed class SceneSaveReport
        {
            public readonly List<string> saved = new List<string>();
            /// <summary>Dirty Untitled scenes. Never saved: saving one always opens a Save-As panel.</summary>
            public readonly List<string> untitled = new List<string>();
            /// <summary>Dirty scenes outside the requested folder, left exactly as they are.</summary>
            public readonly List<string> outOfScope = new List<string>();
            public readonly List<string> failed = new List<string>();

            public bool Ok => failed.Count == 0;

            public string Summary()
            {
                var parts = new List<string>();
                if (failed.Count > 0) parts.Add("Unity could not save " + string.Join(", ", failed) + ".");
                if (saved.Count > 0) parts.Add("Saved " + string.Join(", ", saved) + ".");
                if (untitled.Count > 0)
                    parts.Add("Left unsaved (Untitled, not on disk, not part of any upload; saving would open a Save-As dialog): "
                              + string.Join(", ", untitled) + ".");
                if (outOfScope.Count > 0)
                    parts.Add("Left unsaved (outside this content folder, not part of the upload): "
                              + string.Join(", ", outOfScope) + ".");
                return parts.Count == 0 ? "Open scenes have no unsaved changes." : string.Join(" ", parts);
            }
        }

        /// <summary>
        /// Saves every dirty, loaded scene that has a path and lies inside <paramref name="scopeFolder"/>
        /// (every path-backed scene when it is null), without any prompt. Uses
        /// EditorSceneManager.SaveScene(scene) on scenes that already have a path, which overwrites in
        /// place. Never SaveOpenScenes / SaveScenes / SaveCurrentModifiedScenesIfUserWantsTo: with an
        /// Untitled scene open those show a Save-As panel. Untitled scenes are reported, never saved.
        /// </summary>
        public static SceneSaveReport SaveDirtyScenesWithoutPrompt(string scopeFolder)
        {
            var report = new SceneSaveReport();
            foreach (Scene scene in LoadedDirtyScenes())
            {
                if (IsUntitled(scene))
                {
                    report.untitled.Add(Label(scene));
                    continue;
                }
                if (!string.IsNullOrEmpty(scopeFolder) && !IsUnder(scene.path, scopeFolder))
                {
                    report.outOfScope.Add(scene.path);
                    continue;
                }

                bool ok;
                try { ok = EditorSceneManager.SaveScene(scene); }
                catch (Exception e)
                {
                    Debug.LogWarning("[DreamPark] Could not save " + scene.path + ": " + e.Message);
                    ok = false;
                }
                (ok ? report.saved : report.failed).Add(scene.path);
            }
            return report;
        }

        /// <summary>
        /// Clears the unsaved-changes flag of every loaded scene for the lifetime of the returned object
        /// and puts it back on Dispose. Nothing is saved, discarded or reloaded: the scenes and their
        /// in-memory edits stay exactly as they were.
        ///
        /// For the Addressables build only. DreamPark content never bundles a scene (.unity is
        /// disallowed by ContentProcessor), but Addressables' BuildScriptPackedMode refuses to build, and
        /// shows the modal "Unsaved Scenes" dialog, while any scene is dirty. Check
        /// <see cref="SceneDirtinessSuspension.Error"/>: when the flag cannot be cleared the build must
        /// fail with that message rather than reach the dialog.
        /// </summary>
        public static SceneDirtinessSuspension SuspendSceneDirtiness()
        {
            var suspension = new SceneDirtinessSuspension();
            List<Scene> dirty = LoadedDirtyScenes();
            if (dirty.Count == 0) return suspension;

            MethodInfo clear = typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(Scene) }, null);
            if (clear == null)
            {
                suspension.Error = "These open scenes have unsaved changes and this Unity version cannot set them aside "
                    + "for the build: " + string.Join(", ", dirty.Select(Label))
                    + ". Save or close them, then publish again (Addressables would otherwise stop the build with a modal dialog).";
                return suspension;
            }

            foreach (Scene scene in dirty)
            {
                try
                {
                    clear.Invoke(null, new object[] { scene });
                    suspension.scenes.Add(scene);
                    suspension.labels.Add(Label(scene));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[DreamPark] Could not set aside the unsaved-changes flag of " + Label(scene) + ": " + e.Message);
                }
            }

            List<Scene> stillDirty = LoadedDirtyScenes();
            if (stillDirty.Count > 0)
                suspension.Error = "These open scenes have unsaved changes that could not be set aside for the build: "
                    + string.Join(", ", stillDirty.Select(Label)) + ". Save or close them, then publish again.";
            return suspension;
        }

        public sealed class SceneDirtinessSuspension : IDisposable
        {
            internal readonly List<Scene> scenes = new List<Scene>();
            internal readonly List<string> labels = new List<string>();
            private bool disposed;

            /// <summary>Scenes whose flag was set aside (and is put back on Dispose).</summary>
            public IReadOnlyList<string> Labels => labels;

            /// <summary>Non-null when a dirty scene is left: building now would raise the dialog.</summary>
            public string Error { get; internal set; }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                foreach (Scene scene in scenes)
                {
                    if (!scene.IsValid() || !scene.isLoaded || scene.isDirty) continue;
                    if (!EditorSceneManager.MarkSceneDirty(scene))
                        Debug.LogWarning("[DreamPark] Could not restore the unsaved-changes flag of " + Label(scene)
                                         + ". Its edits are still in memory; save it before closing Unity.");
                }
            }
        }

        /// <summary>A scene opened (or found open) for reading by an automated check.</summary>
        public struct SceneLease
        {
            public Scene scene;
            /// <summary>True when the caller opened it: <see cref="CloseSceneWithoutSaving"/> closes it again.</summary>
            public bool openedHere;
            /// <summary>It was in the hierarchy but unloaded: close back to unloaded instead of removing it.</summary>
            public bool wasUnloadedInHierarchy;
            /// <summary>The scene was dirty the moment it finished opening (OnValidate / ExecuteAlways churn).</summary>
            public bool dirtiedOnLoad;
        }

        /// <summary>
        /// Opens a scene for reading without ever prompting. A scene that is already loaded is used in
        /// place (unsaved edits included) and never closed; anything else is opened additively, which
        /// changes neither the active scene nor any other open scene. Pair with
        /// <see cref="CloseSceneWithoutSaving"/> in a finally. Never RestoreSceneManagerSetup: it reloads the
        /// user's own scenes from disk, which re-runs their load-time churn.
        /// </summary>
        public static SceneLease OpenSceneForReading(string scenePath)
        {
            var lease = new SceneLease();
            Scene existing = SceneManager.GetSceneByPath(scenePath);
            if (existing.IsValid() && existing.isLoaded)
            {
                lease.scene = existing;
                return lease;
            }

            lease.wasUnloadedInHierarchy = existing.IsValid();
            lease.scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            lease.openedHere = lease.scene.IsValid();
            lease.dirtiedOnLoad = lease.openedHere && lease.scene.isDirty;
            return lease;
        }

        /// <summary>
        /// Closes a scene that <see cref="OpenSceneForReading"/> opened, discarding whatever it changed
        /// while open (load-time churn included). Never saves and never prompts. Leaves scenes the
        /// caller did not open alone.
        /// </summary>
        public static bool CloseSceneWithoutSaving(SceneLease lease)
        {
            if (!lease.openedHere) return true;
            Scene scene = lease.scene;
            if (!scene.IsValid() || !scene.isLoaded) return true;

            if (SceneManager.GetActiveScene() == scene)
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene other = SceneManager.GetSceneAt(i);
                    if (other != scene && other.IsValid() && other.isLoaded)
                    {
                        SceneManager.SetActiveScene(other);
                        break;
                    }
                }
            }

            bool closed;
            try { closed = EditorSceneManager.CloseScene(scene, !lease.wasUnloadedInHierarchy); }
            catch (Exception e)
            {
                Debug.LogWarning("[DreamPark] Could not close " + scene.path + " after reading it: " + e.Message);
                closed = false;
            }
            if (!closed) Debug.LogWarning("[DreamPark] Could not close " + scene.path + " after reading it.");
            return closed;
        }

        // ------------------------------------------------------------------ platform

        /// <summary>
        /// Switches the active build target with the synchronous scripting API, which never opens the
        /// Build Profiles window or a dialog, and checks that the switch happened. Returns false with a
        /// message instead of continuing on the wrong target.
        /// </summary>
        public static bool TrySwitchBuildTarget(BuildTargetGroup group, BuildTarget target, out string error)
        {
            error = null;
            if (EditorUserBuildSettings.activeBuildTarget == target) return true;

            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                error = $"Build target {target} is not installed in this Unity Editor. Add the {group} module in Unity Hub, then publish again.";
                return false;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                error = $"Cannot switch to {target} during Play Mode. Exit Play Mode, then publish again.";
                return false;
            }

            bool switched;
            try { switched = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target); }
            catch (Exception e)
            {
                error = $"Switching the build target to {target} failed: {e.Message}";
                return false;
            }

            if (!switched || EditorUserBuildSettings.activeBuildTarget != target)
            {
                error = $"Unity did not switch the build target to {target} (still {EditorUserBuildSettings.activeBuildTarget}). See the Console for the platform's own error.";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The prefab-instance property modifications a scene file holds ON DISK, read from its YAML
    /// without opening it. Used to tell real unapplied overrides from load-time churn when a scene
    /// marks itself dirty as it opens (ArcadeLand's Modular 3D Text rewrote TextUpdater.GUIID and
    /// MeshFilter.m_Mesh on every load, giving nine false scene-overrides warnings).
    /// </summary>
    public static class SceneYamlModifications
    {
        public sealed class InstanceModifications
        {
            public readonly HashSet<string> targetAndPath = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
        }

        private static readonly Regex DocumentHeader = new Regex(@"^--- !u!(\d+) &(-?\d+)", RegexOptions.Compiled);
        private static readonly Regex TargetLine = new Regex(@"^\s*- target: \{fileID: (-?\d+)", RegexOptions.Compiled);
        private static readonly Regex PathLine = new Regex(@"^\s*propertyPath: (.*)$", RegexOptions.Compiled);

        /// <summary>
        /// PrefabInstance fileID → its on-disk modifications. Null when the file is missing or not
        /// text-serialized; callers then keep every modification (fail towards reporting).
        /// </summary>
        public static Dictionary<long, InstanceModifications> Read(string scenePath)
        {
            string full = Path.GetFullPath(scenePath);
            if (!File.Exists(full)) return null;

            string[] lines;
            try { lines = File.ReadAllLines(full); }
            catch { return null; }
            if (lines.Length == 0 || !lines[0].StartsWith("%YAML", StringComparison.Ordinal)) return null;

            var result = new Dictionary<long, InstanceModifications>();
            InstanceModifications current = null;
            string pendingTarget = null;
            foreach (string line in lines)
            {
                Match header = DocumentHeader.Match(line);
                if (header.Success)
                {
                    pendingTarget = null;
                    current = null;
                    if (header.Groups[1].Value == "1001")   // PrefabInstance
                    {
                        long id = long.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
                        current = new InstanceModifications();
                        result[id] = current;
                    }
                    continue;
                }
                if (current == null) continue;

                Match target = TargetLine.Match(line);
                if (target.Success)
                {
                    pendingTarget = target.Groups[1].Value;
                    continue;
                }

                Match path = PathLine.Match(line);
                if (path.Success && pendingTarget != null)
                {
                    string p = path.Groups[1].Value.Trim();
                    current.paths.Add(p);
                    current.targetAndPath.Add(pendingTarget + "|" + p);
                    pendingTarget = null;
                }
            }
            return result;
        }

        /// <summary>The scene-file fileID of the PrefabInstance behind an outermost instance root, or 0.</summary>
        public static long PrefabInstanceFileId(GameObject instanceRoot)
        {
            if (instanceRoot == null) return 0;
            try
            {
                GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(instanceRoot);
                if (id.targetPrefabId != 0) return unchecked((long)id.targetPrefabId);

                UnityEngine.Object handle = PrefabUtility.GetPrefabInstanceHandle(instanceRoot);
                if (handle != null) return unchecked((long)GlobalObjectId.GetGlobalObjectIdSlow(handle).targetObjectId);
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Drops the modifications that are not in the scene file: the ones a script made while the
        /// scene was loading. Keeps everything when the instance cannot be matched to the file.
        /// </summary>
        public static PropertyModification[] WithoutLoadChurn(GameObject instanceRoot, PropertyModification[] modifications,
            Dictionary<long, InstanceModifications> onDisk)
        {
            if (modifications == null || onDisk == null) return modifications;
            long instanceId = PrefabInstanceFileId(instanceRoot);
            InstanceModifications disk;
            if (instanceId == 0 || !onDisk.TryGetValue(instanceId, out disk)) return modifications;

            var keyed = new List<KeyValuePair<PropertyModification, string>>();
            bool anyExact = false;
            foreach (PropertyModification mod in modifications)
            {
                if (mod == null) continue;
                string key = null;
                if (mod.target != null
                    && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mod.target, out string _, out long localId))
                    key = localId.ToString(CultureInfo.InvariantCulture) + "|" + mod.propertyPath;
                if (key != null && disk.targetAndPath.Contains(key)) anyExact = true;
                keyed.Add(new KeyValuePair<PropertyModification, string>(mod, key));
            }

            // When no target id matches at all (an id scheme this reader does not model), compare by
            // property path alone rather than drop real overrides.
            bool exact = anyExact;
            return keyed.Where(kv => exact && kv.Value != null
                    ? disk.targetAndPath.Contains(kv.Value)
                    : disk.paths.Contains(kv.Key.propertyPath))
                .Select(kv => kv.Key)
                .ToArray();
        }
    }
}
#endif
