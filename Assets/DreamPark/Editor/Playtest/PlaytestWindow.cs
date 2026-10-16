#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Playtest
{
    /// <summary>DreamPark → AI Playtester. Configure a run, watch it live, open the report.</summary>
    public class PlaytestWindow : EditorWindow
    {
        static readonly Color Purple = new Color(0.467f, 0f, 1f);

        Vector2 _logScroll, _setupScroll;
        bool _stickToBottom = true;
        bool _showThinking;
        Texture2D _shot;
        byte[] _shotBytes;
        GUIStyle _wrap, _dim, _issueStyle;

        [MenuItem("DreamPark/AI Playtester", false, 2)]
        public static void Open()
        {
            var w = GetWindow<PlaytestWindow>("AI Playtester");
            w.minSize = new Vector2(420, 480);
        }

        void OnEnable() => PlaytestSession.Changed += OnChanged;

        void OnDisable()
        {
            PlaytestSession.Changed -= OnChanged;
            if (_shot != null) DestroyImmediate(_shot);
        }

        void OnChanged() => Repaint();

        void OnInspectorUpdate()
        {
            if (PlaytestSession.IsActive) Repaint();
        }

        void Styles()
        {
            if (_wrap != null) return;
            _wrap = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = false };
            _dim = new GUIStyle(_wrap) { fontStyle = FontStyle.Italic };
            _dim.normal.textColor = new Color(0.55f, 0.55f, 0.6f);
            _issueStyle = new GUIStyle(_wrap) { fontStyle = FontStyle.Bold };
        }

        void OnGUI()
        {
            Styles();
            var s = PlaytestSession.Current;
            DrawHeader(s);

            if (PlaytestSession.IsActive) DrawLive(s);
            else
            {
                _setupScroll = EditorGUILayout.BeginScrollView(_setupScroll, GUILayout.MaxHeight(position.height * 0.55f));
                DrawSetup();
                EditorGUILayout.EndScrollView();
                if (s.entries != null && s.entries.Count > 0)
                {
                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("Last run", EditorStyles.boldLabel);
                    DrawLive(s);
                }
            }
        }

        void DrawHeader(PlaytestSession.State s)
        {
            var rect = EditorGUILayout.GetControlRect(false, 28);
            EditorGUI.DrawRect(rect, Purple);
            var title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
            title.normal.textColor = Color.white;
            GUI.Label(new Rect(rect.x + 8, rect.y + 5, rect.width - 16, 20), "AI Playtester", title);

            string status = s.status == PlaytestSession.Status.Idle ? "Ready" : s.status.ToString();
            if (s.status == PlaytestSession.Status.Running || s.status == PlaytestSession.Status.Finished)
                status += $" · step {s.steps}/{s.maxSteps} · {s.issues.Count} issue{(s.issues.Count == 1 ? "" : "s")}";
            if (!string.IsNullOrEmpty(s.verdict)) status += $" · {s.verdict}";
            var right = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            right.normal.textColor = Color.white;
            GUI.Label(new Rect(rect.x, rect.y, rect.width - 8, rect.height), status, right);
        }

        void DrawSetup()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Claude plays your game as a guest — walking, looking, touching and hitting things with simulated hands — " +
                "reads your Lua to learn how it is supposed to work, and reports what's broken with evidence.", _wrap);
            EditorGUILayout.Space(6);

            var ids = PlaytestBriefing.ContentIds();
            string current = string.IsNullOrEmpty(PlaytestSettings.ContentId) ? PlaytestBriefing.DefaultContentId() : PlaytestSettings.ContentId;
            if (ids.Count > 0)
            {
                int idx = Mathf.Max(0, ids.IndexOf(current));
                int picked = EditorGUILayout.Popup("Content", idx, ids.ToArray());
                PlaytestSettings.ContentId = ids[picked];
                string notes = $"{ContentFolders.Root}/{ids[picked]}/{PlaytestBriefing.NotesFileName}";
                if (!File.Exists(notes))
                {
                    EditorGUILayout.HelpBox($"Tip: add {notes} describing the rules, win condition and what to focus on. The tester treats it as the spec.", MessageType.None);
                }
            }

            EditorGUILayout.LabelField("What should it test?");
            PlaytestSettings.Goal = EditorGUILayout.TextArea(PlaytestSettings.Goal, _wrap, GUILayout.MinHeight(48));
            if (string.IsNullOrWhiteSpace(PlaytestSettings.Goal))
                EditorGUILayout.LabelField("Empty = play the whole game like a first-time guest and report everything.", _dim);

            EditorGUILayout.Space(4);
            int m = Mathf.Max(0, System.Array.IndexOf(PlaytestSettings.Models, PlaytestSettings.Model));
            PlaytestSettings.Model = PlaytestSettings.Models[EditorGUILayout.Popup("Model", m, PlaytestSettings.Models)];
            int e = Mathf.Max(0, System.Array.IndexOf(PlaytestSettings.Efforts, PlaytestSettings.Effort));
            PlaytestSettings.Effort = PlaytestSettings.Efforts[EditorGUILayout.Popup("Effort", e, PlaytestSettings.Efforts)];
            PlaytestSettings.MaxSteps = EditorGUILayout.IntSlider("Max steps", PlaytestSettings.MaxSteps, 10, 200);

            PlaytestSettings.FreezeTime = EditorGUILayout.ToggleLeft(new GUIContent("Freeze game time while Claude thinks", "Real-time games don't punish the tester for model latency."), PlaytestSettings.FreezeTime);
            PlaytestSettings.AutoScreenshot = EditorGUILayout.ToggleLeft(new GUIContent("Screenshot after every action", "Lets Claude see results. Small JPEGs; old ones are pruned."), PlaytestSettings.AutoScreenshot);
            PlaytestSettings.BodyProbe = EditorGUILayout.ToggleLeft(new GUIContent("Body collider (floor pads, walk-in triggers)", "The template rig has no colliders; this adds a floor-to-eye capsule under your PlayerRig."), PlaytestSettings.BodyProbe);
            PlaytestSettings.AllowFixes = EditorGUILayout.ToggleLeft(new GUIContent("Let Claude fix Lua bugs and retest", "Edits only .lua.txt files in the content folder, backs up originals next to the report, and restarts Play mode to verify."), PlaytestSettings.AllowFixes);

            EditorGUILayout.Space(4);
            if (!string.IsNullOrEmpty(PlaytestSettings.EnvApiKey))
            {
                EditorGUILayout.LabelField("API key: using ANTHROPIC_API_KEY from the Editor's environment.", _dim);
            }
            else
            {
                PlaytestSettings.StoredApiKey = EditorGUILayout.PasswordField("Anthropic API key", PlaytestSettings.StoredApiKey);
                EditorGUILayout.LabelField("Stored in this machine's EditorPrefs only — never in the project.", _dim);
            }

            PlaytestSettings.BaseUrl = EditorGUILayout.TextField(new GUIContent("API base URL (optional)", "Route requests through a proxy. Empty = api.anthropic.com. With a proxy, the API key is optional."), PlaytestSettings.BaseUrl);

            EditorGUILayout.Space(8);
            bool playing = EditorApplication.isPlaying;
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(PlaytestSettings.ApiKey) && string.IsNullOrWhiteSpace(PlaytestSettings.BaseUrl)))
            {
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = Purple;
                if (GUILayout.Button(playing ? "▶  Playtest the running game" : "▶  Start Playtest (enters Play mode)", GUILayout.Height(32)))
                {
                    string error = PlaytestSession.Start(PlaytestSession.Options.FromSettings());
                    if (error != null) EditorUtility.DisplayDialog("AI Playtester", error, "OK");
                }
                GUI.backgroundColor = prev;
            }
            if (!playing)
                EditorGUILayout.LabelField("Plays the open scene. To test a package, start it from the Content Uploader's ▶ Test buttons first, then press this.", _dim);
        }

        void DrawLive(PlaytestSession.State s)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PlaytestSession.IsActive && GUILayout.Button("■ Stop", GUILayout.Width(80))) PlaytestSession.Stop();
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(s.reportPath) || !File.Exists(s.reportPath)))
                {
                    if (GUILayout.Button("Open Report")) EditorUtility.OpenWithDefaultApp(Path.GetFullPath(s.reportPath));
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(s.runDir) || !Directory.Exists(s.runDir)))
                {
                    if (GUILayout.Button("Show Folder")) EditorUtility.RevealInFinder(Path.GetFullPath(s.runDir));
                }
                GUILayout.FlexibleSpace();
                _showThinking = GUILayout.Toggle(_showThinking, "Reasoning", EditorStyles.miniButton, GUILayout.Width(80));
            }

            DrawScreenshot();

            if (s.issues.Count > 0)
            {
                EditorGUILayout.LabelField($"Issues ({s.issues.Count})", EditorStyles.boldLabel);
                foreach (var i in s.issues)
                {
                    var prev = GUI.contentColor;
                    GUI.contentColor = SeverityColor(i.severity);
                    EditorGUILayout.LabelField($"[{i.severity}] {i.title}", _issueStyle);
                    GUI.contentColor = prev;
                }
            }

            EditorGUILayout.LabelField("Play-by-play", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.ExpandHeight(true));
            foreach (var e in s.entries)
            {
                if (e.kind == "thinking" && !_showThinking) continue;
                DrawEntry(e);
            }
            EditorGUILayout.EndScrollView();
            if (PlaytestSession.IsActive && _stickToBottom && Event.current.type == EventType.Repaint)
                _logScroll.y = float.MaxValue;
        }

        void DrawEntry(PlaytestSession.Entry e)
        {
            switch (e.kind)
            {
                case "say": EditorGUILayout.LabelField(e.text, _wrap); break;
                case "thinking": EditorGUILayout.LabelField(e.text, _dim); break;
                case "action": EditorGUILayout.LabelField("→ " + e.text, EditorStyles.miniBoldLabel); break;
                case "result": EditorGUILayout.LabelField("   " + e.text, EditorStyles.wordWrappedMiniLabel); break;
                case "issue":
                {
                    var prev = GUI.contentColor;
                    GUI.contentColor = new Color(1f, 0.55f, 0.3f);
                    EditorGUILayout.LabelField("⚑ " + e.text, _issueStyle);
                    GUI.contentColor = prev;
                    break;
                }
                case "fix": EditorGUILayout.LabelField("✎ " + e.text, _issueStyle); break;
                case "error": EditorGUILayout.HelpBox(e.text, MessageType.Error); break;
                default: EditorGUILayout.LabelField(e.text, _dim); break;
            }
        }

        void DrawScreenshot()
        {
            var bytes = PlaytestSession.LastScreenshot;
            if (bytes == null) return;
            if (!ReferenceEquals(bytes, _shotBytes))
            {
                _shotBytes = bytes;
                if (_shot == null) _shot = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                _shot.LoadImage(bytes);
            }
            float w = position.width - 12;
            float h = Mathf.Min(w * _shot.height / Mathf.Max(1, _shot.width), 260);
            var rect = GUILayoutUtility.GetRect(w, h);
            GUI.DrawTexture(rect, _shot, ScaleMode.ScaleToFit);
        }

        static Color SeverityColor(string severity)
        {
            switch (severity)
            {
                case "blocker": return new Color(1f, 0.35f, 0.35f);
                case "major": return new Color(1f, 0.6f, 0.3f);
                case "minor": return new Color(1f, 0.85f, 0.4f);
                default: return new Color(0.7f, 0.8f, 1f);
            }
        }
    }
}
#endif
