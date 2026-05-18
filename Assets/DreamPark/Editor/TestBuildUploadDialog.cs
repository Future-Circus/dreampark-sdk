#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Small modal-style EditorWindow for collecting a title + release notes
    // + the editor platforms to compile for before a Test Channel upload.
    //
    // Test builds are intended for previewing inside dreampark-core's
    // Content Manager (which runs in the Unity editor on Mac or Windows),
    // so the platform options are limited to the two editor targets —
    // iOS and Android builds aren't useful here and would just burn build
    // time. The platform toggles default to whichever of Mac/Windows was
    // last used (or both on, on first use) and persist via EditorPrefs so
    // the choice carries over between sessions.
    //
    // Defaults the title to the current park name (Assets/Content/{GameName})
    // plus a short timestamp so a one-click "Upload Test Build" still
    // produces a meaningful entry in the Test Channel listing without the
    // user typing anything.
    public class TestBuildUploadDialog : EditorWindow
    {
        private const string BuildOsxPrefKey = "DreamPark.TestBuildUpload.BuildOsx";
        private const string BuildWindowsPrefKey = "DreamPark.TestBuildUpload.BuildWindows";

        private string title = "";
        private string releaseNotes = "";
        private string contentName = "";
        private bool buildOsx = true;
        private bool buildWindows = true;
        // Callback signature: (title, releaseNotes, contentName, buildOsx, buildWindows).
        // contentName carries the originating park name through to the
        // commit metadata so the Test Channel listing can show "{title}
        // · {contentName}" the same way a production card shows the
        // ContentName field.
        private Action<string, string, string, bool, bool> onConfirm;
        private bool didInitialFocus;

        public static void Show(string defaultContentName, Action<string, string, string, bool, bool> onConfirm)
        {
            var window = CreateInstance<TestBuildUploadDialog>();
            window.titleContent = new GUIContent("Upload Test Build");
            window.contentName = defaultContentName ?? "";
            window.title = BuildDefaultTitle(defaultContentName);
            window.releaseNotes = "";
            window.onConfirm = onConfirm;
            // Restore last-used platform selection; default both on so a
            // fresh install builds everything the editor can run.
            window.buildOsx = EditorPrefs.GetBool(BuildOsxPrefKey, true);
            window.buildWindows = EditorPrefs.GetBool(BuildWindowsPrefKey, true);
            window.minSize = new Vector2(440, 320);
            window.maxSize = new Vector2(440, 320);
            window.ShowUtility();
        }

        private static string BuildDefaultTitle(string parkName)
        {
            string safePark = string.IsNullOrWhiteSpace(parkName) ? "TestBuild" : parkName;
            string stamp = DateTime.Now.ToString("MMM d, h:mm tt");
            return $"{safePark} — {stamp}";
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Test builds compile editor-only addressables (Mac and/or Windows) and push them to the " +
                "Test Channel in dreampark-core's Content Manager. iOS and Android are skipped because " +
                "test builds are for previewing inside the Unity editor. Auto-expires after 7 days. " +
                "Admin / DreamPark teammates only.",
                MessageType.Info);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Title", EditorStyles.boldLabel);
            GUI.SetNextControlName("TestBuildTitleField");
            title = EditorGUILayout.TextField(title);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Release Notes (optional)", EditorStyles.boldLabel);
            releaseNotes = EditorGUILayout.TextArea(releaseNotes, GUILayout.Height(60));

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Editor Targets", EditorStyles.boldLabel);
            bool newOsx = EditorGUILayout.ToggleLeft("Editor (Mac) — StandaloneOSX", buildOsx);
            if (newOsx != buildOsx)
            {
                buildOsx = newOsx;
                EditorPrefs.SetBool(BuildOsxPrefKey, buildOsx);
            }
            bool newWindows = EditorGUILayout.ToggleLeft("Editor (Windows) — StandaloneWindows", buildWindows);
            if (newWindows != buildWindows)
            {
                buildWindows = newWindows;
                EditorPrefs.SetBool(BuildWindowsPrefKey, buildWindows);
            }

            if (!buildOsx && !buildWindows)
            {
                EditorGUILayout.HelpBox("Pick at least one editor target.", MessageType.Warning);
            }

            GUILayout.FlexibleSpace();
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(80), GUILayout.Height(24)))
                {
                    Close();
                }
                bool canConfirm = !string.IsNullOrWhiteSpace(title) && (buildOsx || buildWindows);
                using (new EditorGUI.DisabledScope(!canConfirm))
                {
                    if (GUILayout.Button("Compile & Upload", GUILayout.Width(160), GUILayout.Height(24)))
                    {
                        var confirmCallback = onConfirm;
                        var t = title.Trim();
                        var notes = releaseNotes ?? "";
                        var cn = contentName ?? "";
                        var osx = buildOsx;
                        var win = buildWindows;
                        Close();
                        confirmCallback?.Invoke(t, notes, cn, osx, win);
                    }
                }
            }

            // Focus the title field on first paint so the user can start
            // typing immediately without clicking. Only once — re-focusing
            // every frame would steal focus from the release-notes field
            // mid-typing.
            if (!didInitialFocus && Event.current.type == EventType.Repaint)
            {
                didInitialFocus = true;
                EditorGUI.FocusTextInControl("TestBuildTitleField");
            }
        }
    }
}
#endif
