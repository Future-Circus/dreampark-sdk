// ─────────────────────────────────────────────────────────────────────
//  ConvertReadyReportWindow.cs — what the converter decided, and how sure
//
//  This is deliberately NOT a summary. "Converted 3 assets ✓" is worthless:
//  it tells a creator nothing about the two places the tool guessed, and it
//  is exactly the kind of dialog people learn to dismiss without reading.
//
//  So the report is a list of DECISIONS, each tagged by how it was reached:
//
//    MEASURED   derived from the asset. Reproducible. Trust it.
//    GUESSED    a heuristic. You confirmed it, and you can undo it.
//    ADDED      a component or object we created.
//    EXTRACTED  something we pulled out of the source asset.
//    SKIPPED    deliberately not done, with the reason.
//    FAILED     did not work, with the error.
//
//  Separating MEASURED from GUESSED is the whole point. There are exactly two
//  guesses in this pipeline — unit interpretation and up-axis — and both are
//  confirmed by the creator before anything is written. Everything else is
//  derived. A creator who can see that distinction keeps using the tool after
//  it gets one wrong; a creator who cannot, does not.
//
//  The window is non-modal on purpose. The useful next action is often "look
//  at the prefab, then undo", and a modal dialog prevents exactly that.
//
//  ON THE REVERT BUTTON — do not replace it with "press ⌘Z"
//
//  Unity's Undo covers SCENE objects. It does NOT cover asset creation:
//  PrefabUtility.SaveAsPrefabAsset and AssetDatabase.CreateAsset are not
//  undoable and never have been. An earlier draft of this window told the
//  creator "⌘Z undoes this entire run, including the created prefab assets".
//  That is a comfortable sentence and a false one — the prefabs survive, and
//  the creator walks away believing they reverted something they did not.
//
//  So the revert here is explicit: it deletes the assets the run created, by
//  path, via ConvertReadyExecutor.RevertLastRun.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public class ConvertReadyReportWindow : EditorWindow
    {
        private ConversionReport report;
        private List<string> createdAssets = new List<string>();
        private Vector2 scroll;
        private readonly HashSet<int> collapsed = new HashSet<int>();

        public static void Show(ConversionReport report, IList<string> createdAssets)
        {
            if (report == null || report.results.Count == 0) return;

            var w = GetWindow<ConvertReadyReportWindow>(false, "Convert to DreamPark-Ready", true);
            w.report = report;
            w.createdAssets = createdAssets != null
                ? new List<string>(createdAssets)
                : new List<string>();
            w.minSize = new Vector2(520, 320);
            w.collapsed.Clear();
            w.Repaint();
        }

        // Colours are picked to survive both editor skins. The pro skin washes
        // out saturated greens and the light skin washes out pale ones, so
        // these sit in the middle where both are legible.
        private static Color ColorFor(DecisionKind kind)
        {
            switch (kind)
            {
                case DecisionKind.Measured:  return new Color(0.45f, 0.78f, 0.45f);
                case DecisionKind.Guessed:   return new Color(0.95f, 0.72f, 0.25f);
                case DecisionKind.Added:     return new Color(0.45f, 0.70f, 0.95f);
                case DecisionKind.Extracted: return new Color(0.70f, 0.60f, 0.95f);
                case DecisionKind.Skipped:   return new Color(0.60f, 0.60f, 0.60f);
                default:                     return new Color(0.95f, 0.42f, 0.38f);
            }
        }

        private void OnGUI()
        {
            if (report == null)
            {
                EditorGUILayout.HelpBox("Nothing to report.", MessageType.Info);
                return;
            }

            DrawHeader();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < report.results.Count; i++)
            {
                DrawResult(i, report.results[i]);
            }
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        private void DrawHeader()
        {
            int ok = report.OkCount;
            int failed = report.FailedCount;

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                var style = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
                GUILayout.Label(failed == 0
                    ? ok + (ok == 1 ? " asset converted" : " assets converted")
                    : ok + " converted, " + failed + " failed", style);
                GUILayout.FlexibleSpace();
            }

            // Guess count up front. If the tool guessed at all, the creator
            // should know before they scroll, because those are the lines worth
            // reading.
            int guesses = 0;
            foreach (var r in report.results)
                foreach (var d in r.decisions)
                    if (d.kind == DecisionKind.Guessed) guesses++;

            if (guesses > 0)
            {
                EditorGUILayout.HelpBox(
                    guesses + (guesses == 1 ? " decision was a guess" : " decisions were guesses")
                    + " — they are highlighted below. Everything else was measured from the asset.",
                    MessageType.None);
            }

            EditorGUILayout.Space(2);
        }

        private void DrawResult(int index, ConversionResult r)
        {
            bool isCollapsed = collapsed.Contains(index);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string title = r.outputPath != null
                        ? System.IO.Path.GetFileNameWithoutExtension(r.outputPath)
                        : System.IO.Path.GetFileName(r.sourcePath);

                    if (GUILayout.Button(isCollapsed ? "▸" : "▾",
                            EditorStyles.label, GUILayout.Width(16)))
                    {
                        if (isCollapsed) collapsed.Remove(index);
                        else collapsed.Add(index);
                    }

                    GUILayout.Label(title, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();

                    if (!r.ok)
                    {
                        var prev = GUI.color;
                        GUI.color = ColorFor(DecisionKind.Failed);
                        GUILayout.Label("FAILED", EditorStyles.miniBoldLabel);
                        GUI.color = prev;
                    }
                    else if (r.outputPath != null)
                    {
                        if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(56)))
                        {
                            var obj = AssetDatabase.LoadAssetAtPath<Object>(r.outputPath);
                            if (obj != null)
                            {
                                Selection.activeObject = obj;
                                EditorGUIUtility.PingObject(obj);
                            }
                        }
                    }
                }

                if (isCollapsed) return;

                if (r.outputPath != null)
                {
                    EditorGUILayout.LabelField(r.outputPath, EditorStyles.miniLabel);
                }

                EditorGUILayout.Space(2);

                foreach (var d in r.decisions)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(6);
                        var prev = GUI.color;
                        GUI.color = ColorFor(d.kind);
                        GUILayout.Label(d.Label, EditorStyles.miniBoldLabel, GUILayout.Width(76));
                        GUI.color = prev;
                        GUILayout.Label(d.message, EditorStyles.wordWrappedMiniLabel);
                    }
                }
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(6);

                using (new EditorGUI.DisabledScope(createdAssets.Count == 0))
                {
                    if (GUILayout.Button(
                            new GUIContent(
                                "Delete " + createdAssets.Count + " created asset"
                                + (createdAssets.Count == 1 ? "" : "s"),
                                "Undo does not cover asset creation in Unity, so this deletes them "
                                + "by path instead. Scene-side changes are covered by ⌘Z."),
                            EditorStyles.miniButton, GUILayout.Width(180)))
                    {
                        if (EditorUtility.DisplayDialog(
                                "Delete created assets?",
                                "This permanently deletes the " + createdAssets.Count
                                + " asset(s) this run created:\n\n"
                                + string.Join("\n", createdAssets.ToArray())
                                + "\n\nUnity's Undo does not cover asset creation, which is why "
                                + "this is a separate action.",
                                "Delete", "Cancel"))
                        {
                            int n = ConvertReadyExecutor.RevertLastRun(createdAssets);
                            createdAssets.Clear();
                            ShowNotification(new GUIContent(n + " asset(s) deleted"));
                        }
                    }
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Copy report", EditorStyles.miniButton, GUILayout.Width(90)))
                {
                    EditorGUIUtility.systemCopyBuffer = report.ToPlainText();
                }
                if (GUILayout.Button("Close", GUILayout.Width(70)))
                {
                    Close();
                }
                GUILayout.Space(6);
            }
            EditorGUILayout.Space(6);
        }
    }
}
#endif
