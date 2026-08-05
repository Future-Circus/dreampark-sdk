#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DreamPark
{
    // Opens a chosen scene the first time the editor comes up on a project.
    //
    // WHY THIS IS NEEDED
    //
    // Unity restores whatever scene setup was open when the project last closed, from
    // state cached under Library/. A freshly cloned SDK has no Library/, so Unity
    // opens an empty Untitled scene and the creator's first experience is a blank
    // grey void. There is no built-in project setting for "always open this scene".
    //
    // WHY SessionState AND NOT EditorPrefs FOR THE GUARD
    //
    // SessionState survives a domain reload and is cleared when the editor exits —
    // exactly "once per editor launch". Using EditorPrefs would fire once ever; using
    // a plain static would fire on every script recompile, which happens constantly.
    //
    // The configured scene itself IS stored on disk rather than in EditorPrefs,
    // because EditorPrefs is per-machine and the entire point is that a fresh clone
    // lands somewhere useful for whoever cloned it.
    [InitializeOnLoad]
    internal static class StartupSceneOpener
    {
        private const string SessionFlag = "DreamPark.StartupScene.HandledThisSession";

        static StartupSceneOpener()
        {
            EditorApplication.delayCall += MaybeOpen;
        }

        private static void MaybeOpen()
        {
            // Mark BEFORE acting, so a throw below can't turn into a retry loop.
            if (SessionState.GetBool(SessionFlag, false)) return;

            // Re-arm rather than give up. On the exact case this exists for — a fresh
            // clone with no Library/ — the first tick lands mid-import, so returning
            // here would mean the startup scene never opens and the creator still gets
            // the grey void.
            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += MaybeOpen;
                return;
            }

            SessionState.SetBool(SessionFlag, true);

            try
            {
                var settings = StartupSceneSettings.Load();
                if (settings.mode == StartupSceneMode.Never) return;

                string target = ResolveTargetScene(settings);
                if (string.IsNullOrEmpty(target)) return;
                if (!File.Exists(target))
                {
                    Debug.LogWarning($"[DreamPark] Startup scene '{target}' does not exist. " +
                                     "Set a new one via DreamPark ▸ Startup Scene.");
                    return;
                }

                var active = SceneManager.GetActiveScene();

                // Already there.
                if (string.Equals(active.path, target, StringComparison.OrdinalIgnoreCase)) return;

                // Never stomp work in progress.
                if (active.isDirty) return;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty) return;

                // FirstOpenOnly means "only when Unity had nothing to restore" — a
                // fresh clone, or a wiped Library/. An empty path is Unity's untitled
                // scene. This is the case the feature is actually for, and it can
                // never take away a scene someone deliberately left open.
                if (settings.mode == StartupSceneMode.FirstOpenOnly &&
                    !string.IsNullOrEmpty(active.path))
                    return;

                EditorSceneManager.OpenScene(target, OpenSceneMode.Single);
                Debug.Log($"[DreamPark] Opened startup scene: {target}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not open the startup scene: {e.Message}");
            }
        }

        private static string ResolveTargetScene(StartupSceneSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.startupScene)) return settings.startupScene;

            // Convention fallback: the first scene under the selected content folder.
            // Zero configuration for the common case — a fresh SDK clone lands in the
            // template scene without anyone setting anything up.
            string contentId = PreUploadChecks.ContentRootScanner.CurrentContentId();
            if (string.IsNullOrEmpty(contentId)) return null;

            string root = PreUploadChecks.ContentRootScanner.RootFor(contentId);
            if (!AssetDatabase.IsValidFolder(root)) return null;

            var scenes = AssetDatabase.FindAssets("t:Scene", new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Where(p => !PreUploadChecks.ContentRootScanner.IsThirdPartyLocal(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return scenes.Count > 0 ? scenes[0] : null;
        }
    }

    internal enum StartupSceneMode
    {
        FirstOpenOnly = 0,   // only when Unity restored nothing (fresh clone / wiped Library)
        Always = 1,          // every editor launch, unless a scene is dirty
        Never = 2,
    }

    // Stored at Assets/.dreampark-editor.json.
    //
    // Dot-prefixed for the same reason PreviewMetadataStore uses that convention:
    // Unity's asset pipeline ignores files whose name starts with a dot, so no .meta
    // is emitted, no GUID is minted, and it can never end up in a bundle — while git
    // still tracks it, so the whole team (and every fresh clone) gets the same
    // startup scene. EditorPrefs would be per-machine, which defeats the purpose.
    [Serializable]
    internal class StartupSceneSettings
    {
        private const string SettingsPath = "Assets/.dreampark-editor.json";

        public string startupScene = "";
        public StartupSceneMode mode = StartupSceneMode.FirstOpenOnly;

        public static StartupSceneSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new StartupSceneSettings();
                var loaded = JsonUtility.FromJson<StartupSceneSettings>(File.ReadAllText(SettingsPath));
                return loaded ?? new StartupSceneSettings();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not read {SettingsPath}: {e.Message}");
                return new StartupSceneSettings();
            }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(SettingsPath, JsonUtility.ToJson(this, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not write {SettingsPath}: {e.Message}");
            }
        }
    }

    internal class StartupScenePickerWindow : EditorWindow
    {
        private StartupSceneSettings settings;
        private SceneAsset picked;

        [MenuItem("DreamPark/Startup Scene...", false, 2)]
        public static void Open()
        {
            var existing = Resources.FindObjectsOfTypeAll<StartupScenePickerWindow>();
            if (existing != null && existing.Length > 0)
            {
                existing[0].Focus();
                return;
            }

            var win = CreateInstance<StartupScenePickerWindow>();
            win.titleContent = new GUIContent("Startup Scene");
            win.minSize = new Vector2(460f, 220f);
            win.maxSize = new Vector2(460f, 220f);

            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(main.x + (main.width - 460f) / 2f,
                                    main.y + (main.height - 220f) / 2f, 460f, 220f);
            win.ShowUtility();
        }

        private void OnEnable()
        {
            settings = StartupSceneSettings.Load();
            if (!string.IsNullOrEmpty(settings.startupScene))
                picked = AssetDatabase.LoadAssetAtPath<SceneAsset>(settings.startupScene);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Startup Scene", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Which scene to open when this project is first loaded. Saved to "
                + "Assets/.dreampark-editor.json, which is tracked by git — so a fresh clone of this "
                + "repo lands in the right place for everyone.",
                EditorStyles.wordWrappedMiniLabel);

            GUILayout.Space(8);

            picked = (SceneAsset)EditorGUILayout.ObjectField("Scene", picked, typeof(SceneAsset), false);

            if (picked == null)
            {
                EditorGUILayout.HelpBox(
                    "Leave empty to use the first scene found in your content folder.",
                    MessageType.None);
            }

            settings.mode = (StartupSceneMode)EditorGUILayout.EnumPopup("When", settings.mode);

            switch (settings.mode)
            {
                case StartupSceneMode.FirstOpenOnly:
                    EditorGUILayout.LabelField(
                        "Only when Unity had no scene to restore — a fresh clone, or a wiped Library "
                        + "folder. Never replaces a scene you deliberately left open.",
                        EditorStyles.wordWrappedMiniLabel);
                    break;
                case StartupSceneMode.Always:
                    EditorGUILayout.LabelField(
                        "Every time the editor launches, unless a scene has unsaved changes.",
                        EditorStyles.wordWrappedMiniLabel);
                    break;
            }

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", GUILayout.Height(26))) Close();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Save", GUILayout.Height(26), GUILayout.Width(120)))
            {
                settings.startupScene = picked != null ? AssetDatabase.GetAssetPath(picked) : "";
                settings.Save();
                Close();
            }
            GUILayout.EndHorizontal();
        }
    }
}
#endif
