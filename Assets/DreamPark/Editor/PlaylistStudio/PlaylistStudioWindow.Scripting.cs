#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// Playlist Studio: playlist SCRIPTING (repair Oct 20 2026, CONTRACT § 2–§ 5).
    /// <list type="bullet">
    /// <item>Library → the selected attraction's events and its goal blocks per challenge kind, with Generate (the Playlist
    /// API, from the Game Graph), Edit (inline, saved to .dreampark-goals) and Test (the XLua VM against fake events).</item>
    /// <item>Script tab → the playlist's Lua, Dreamie script and timing (unknown-to-the-model fields, kept by Save), each
    /// stop's goal (default / inline), and "Generate scripting".</item>
    /// <item>Campaign tab → what each playlist's Lua sets (flags, badges) and locks.</item>
    /// <item>Toolbar → Pull / Push with the backend playlist store (the source of truth).</item>
    /// </list>
    /// </summary>
    public sealed partial class PlaylistStudioWindow
    {
        [SerializeField] string libSelected;
        string goalEditKey, goalEditText;
        Vector2 goalScroll, scriptScroll, testScroll;
        PlaylistGoalTest.Result goalTest;
        string goalTestKey;
        bool backendBusy;
        string scriptLuaText, scriptDreamieText, scriptTimingText, scriptFor;

        static readonly string[] GoalKinds = { "collect", "reach_end", "crossing", "jump", "boss", "clear", "survive", "score", "race", "versus",
            "catch", "find", "solve", "protect", "moment" };

        // ───────────────────────────── toolbar: backend ─────────────────────────────

        void DrawBackendButtons()
        {
            using (new EditorGUI.DisabledScope(backendBusy || session == null))
            {
                if (GUILayout.Button(new GUIContent("Pull", "Replace the working copy with the backend's playlists, block and goal blocks (the source of truth). Save writes them to the repo export."), EditorStyles.toolbarButton))
                {
                    if (session.dirty && !EditorUtility.DisplayDialog("Pull from DreamPark", "Your unsaved edits will be replaced by the backend's playlists.", "Pull", "Cancel")) return;
                    backendBusy = true; status = "Pulling " + contentId + " from DreamPark…";
                    PlaylistStudioBackend.Pull(contentId, (ok, body, msg) =>
                    {
                        backendBusy = false;
                        if (!ok) { status = "Pull: " + msg; Repaint(); return; }
                        S.TakeBackend(body);
                        status = $"Pulled {S.data.playlists.Count} playlist(s) and {S.goals.Count} goal block(s){(msg != null ? " (" + msg + ")" : "")}. Save to write the repo export.";
                        fitCache.Clear(); Recheck();
                    });
                }
                if (GUILayout.Button(new GUIContent("Push", "Write every playlist, the block (kinds, campaigns, flags) and every goal block to the backend. All or nothing."), EditorStyles.toolbarButton))
                {
                    int errors = issues.Count(i => i.isError);
                    if (errors > 0 && !EditorUtility.DisplayDialog("Push to DreamPark", $"{errors} validation error(s): the backend will refuse the push. Push anyway?", "Push", "Cancel")) return;
                    backendBusy = true; status = "Pushing " + contentId + " to DreamPark…";
                    PlaylistStudioBackend.Push(contentId, S.BackendBody(), (ok, body, msg) =>
                    {
                        backendBusy = false;
                        if (!ok)
                        {
                            var errs = body?.GetField("errors")?.list?.Select(e => (e.GetField("at")?.stringValue ?? "") + ": " + (e.GetField("message")?.stringValue ?? "")).Take(4);
                            status = "Push: " + msg + (errs != null && errs.Any() ? " — " + string.Join("; ", errs) : "");
                        }
                        else status = $"Pushed {body.GetField("playlists")?.count ?? 0} playlist(s), {body.GetField("goals")?.count ?? 0} goal block(s).";
                        Repaint();
                    });
                }
            }
        }

        // ───────────────────────────── library: events + goal blocks ─────────────────────────────

        void SelectLibraryItem(PlaylistLibraryItem item)
        {
            if (item == null || libSelected == item.leaf) return;
            string leaf = item.leaf;
            // after this GUI pass: the library's layout changes with the selection
            EditorApplication.delayCall += () => { libSelected = leaf; goalEditKey = null; goalTest = null; Repaint(); };
        }

        /// The kinds an attraction offers goal blocks for: its catalog/inferred challenges + every kind it already has a block for.
        List<string> GoalKindsOf(PlaylistLibraryItem item)
        {
            var kinds = new List<string>();
            foreach (string c in item.challenges) if (GoalKinds.Contains(c) && !kinds.Contains(c)) kinds.Add(c);
            foreach (string k in PlaylistGoalStore.KindsFor(S.goals, item.leaf)) if (!kinds.Contains(k)) kinds.Add(k);
            if (kinds.Count == 0)
                foreach (string o in item.objectives)
                {
                    string k = o == "reach_exit" ? "reach_end" : o == "defeat_boss" ? "boss" : o == "time" ? "moment" : o;
                    if (GoalKinds.Contains(k) && !kinds.Contains(k)) kinds.Add(k);
                }
            return kinds;
        }

        void DrawGoalPanel()
        {
            PlaylistLibraryItem item = S.library.FirstOrDefault(i => i.leaf == libSelected);
            if (item == null) { GUILayout.Label("Click an attraction to see its events and goal blocks.", EditorStyles.centeredGreyMiniLabel); return; }
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            goalScroll = EditorGUILayout.BeginScrollView(goalScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(item.Name + "  ·  goal blocks", EditorStyles.boldLabel);
            if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22f))) { libSelected = null; GUIUtility.ExitGUI(); }
            EditorGUILayout.EndHorizontal();
            GUILayout.Label("Events its scripts send: " + (item.luaSignals.Count > 0 ? string.Join(", ", item.luaSignals) : "none found (the platform's taps still apply)"), EditorStyles.wordWrappedMiniLabel);
            var kinds = GoalKindsOf(item);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(backendBusy))
                if (GUILayout.Button(new GUIContent("Generate all", "Generate a block for every kind this attraction supports, from the title's Game Graph (DreamPark Playlist API). Existing edited blocks are kept."), EditorStyles.miniButton))
                    GenerateGoals(item, null);
            int add = EditorGUILayout.Popup(0, new[] { "+ kind…" }.Concat(GoalKinds.Where(k => !kinds.Contains(k))).ToArray(), GUILayout.Width(90f));
            if (add > 0) { string k = GoalKinds.Where(x => !kinds.Contains(x)).ElementAt(add - 1); GenerateGoals(item, k); }
            EditorGUILayout.EndHorizontal();
            foreach (string kind in kinds)
            {
                string key = PlaylistGoalStore.Key(item.leaf, kind);
                bool has = S.goals.TryGetValue(key, out string lua);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(PlaylistChallenges.Name(kind), key), GUILayout.Width(98f));
                GUILayout.Label(!has ? "none" : PlaylistGoalStore.SourceOf(lua) + (S.dirtyGoals.Contains(key) ? "*" : ""), EditorStyles.miniLabel, GUILayout.Width(56f));
                using (new EditorGUI.DisabledScope(backendBusy))
                    if (GUILayout.Button(new GUIContent("Generate", "From the Game Graph (replaces this block in the working copy)"), EditorStyles.miniButtonLeft, GUILayout.Width(62f))) GenerateGoals(item, kind);
                using (new EditorGUI.DisabledScope(!has))
                {
                    if (GUILayout.Button(goalEditKey == key ? "Close" : "Edit", EditorStyles.miniButtonMid, GUILayout.Width(44f)))
                    { if (goalEditKey == key) goalEditKey = null; else { goalEditKey = key; goalEditText = lua; } GUIUtility.ExitGUI(); }
                    if (GUILayout.Button(new GUIContent("Test", "Run it in the XLua VM against this kind's fake events (no scene)"), EditorStyles.miniButtonRight, GUILayout.Width(40f)))
                    { TestGoal(key, goalEditKey == key ? goalEditText : lua, kind, item.leaf); GUIUtility.ExitGUI(); }
                }
                EditorGUILayout.EndHorizontal();
                if (goalTestKey == key && goalTest != null) DrawTestResult(goalTest);
                if (goalEditKey == key)
                {
                    goalEditText = EditorGUILayout.TextArea(goalEditText ?? "", MonoText(), GUILayout.MinHeight(160f));
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Apply", EditorStyles.miniButtonLeft, GUILayout.Width(60f)))
                    {
                        string err = PlaylistGoalTest.Compile(goalEditText, key);
                        if (err != null) status = "Not applied: " + err;
                        else { S.SetGoal(key, goalEditText); status = key + " updated in the working copy (Save writes it)."; }
                    }
                    if (GUILayout.Button("Revert", EditorStyles.miniButtonMid, GUILayout.Width(60f))) goalEditText = lua;
                    if (GUILayout.Button("Delete", EditorStyles.miniButtonRight, GUILayout.Width(60f)) && EditorUtility.DisplayDialog("Delete goal block", "Delete " + key + "?", "Delete", "Keep"))
                    { S.SetGoal(key, null); goalEditKey = null; GUIUtility.ExitGUI(); }
                    EditorGUILayout.EndHorizontal();
                }
            }
            if (kinds.Count == 0) GUILayout.Label("No challenge kinds known for it yet: add one above.", EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        static GUIStyle s_mono;
        static GUIStyle MonoText()
        {
            if (s_mono != null) return s_mono;
            s_mono = new GUIStyle(EditorStyles.textArea) { wordWrap = true, richText = false, fontSize = 11 };   // wraps: never widens the pane
            Font f = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "Consolas", "Courier New" }, 11);
            if (f != null) s_mono.font = f;
            return s_mono;
        }

        void GenerateGoals(PlaylistLibraryItem item, string kind)
        {
            backendBusy = true;
            status = $"Generating {(kind ?? "every")} goal block for {item.Name}…";
            PlaylistStudioBackend.GenerateGoals(contentId, item.leaf, kind, (ok, body, msg) =>
            {
                backendBusy = false;
                if (!ok) { status = "Goal generation: " + msg; Repaint(); return; }
                JSONObject blocks = body.GetField("blocks");
                int n = 0, kept = 0;
                for (int i = 0; i < (blocks?.count ?? 0); i++)
                {
                    string key = blocks.keys[i], lua = blocks.list[i].GetField("lua")?.stringValue;
                    if (lua == null) continue;
                    // "Generate all" never replaces a hand-edited block; a per-kind Generate does (that is what was asked)
                    if (kind == null && S.goals.TryGetValue(key, out string had) && PlaylistGoalStore.SourceOf(had) == "edited") { kept++; continue; }
                    S.SetGoal(key, lua); n++;
                }
                string warn = body.GetField("warnings")?.list?.FirstOrDefault()?.stringValue;
                status = $"{n} goal block(s) generated for {item.Name}{(kept > 0 ? $", {kept} edited kept" : "")} (Game Graph {body.GetField("graphRevision")?.stringValue}). Test them, then Save."
                    + (warn != null ? " " + warn : "");
                Repaint();
            });
        }

        void TestGoal(string key, string lua, string kind, string attraction)
        {
            goalTestKey = key;
            goalTest = PlaylistGoalTest.Run(lua, kind, null, null, attraction);
            status = key + ": " + goalTest.Summary;
            Repaint();
        }

        void DrawTestResult(PlaylistGoalTest.Result r)
        {
            EditorGUILayout.HelpBox(r.Summary, r.error != null || !r.ok ? MessageType.Error : r.settled ? MessageType.Info : MessageType.Warning);
            testScroll = EditorGUILayout.BeginScrollView(testScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, GUILayout.MaxHeight(140f));
            foreach (string line in r.lines) GUILayout.Label(line, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        // ───────────────────────────── script tab ─────────────────────────────

        void DrawScriptingTab()
        {
            PackagePlaylist p = Current;
            if (p == null) { EditorGUILayout.HelpBox("Pick a playlist (Playlists tab) to see its scripting.", MessageType.Info); return; }
            if (scriptFor != p.id) LoadScriptBuffers(p);
            scriptScroll = EditorGUILayout.BeginScrollView(scriptScroll, false, false);
            EditorGUILayout.BeginVertical(GUILayout.MaxWidth(position.width - 24f));
            EditorGUILayout.HelpBox("A playlist decides its goals: each stop runs a goal block (the title default for its kind, or its own inline block), "
                + "the playlist Lua runs on the leader (on_start / on_stop_result / choose_next / on_finale; run.lock / run.unlock, run.flag), "
                + "and Dreamie follows the script (story, beats, react, avoid) live. Generate writes all three from the Game Graph; "
                + "anything you author is kept. CONTRACT: dreampark-core .claude/repair-oct20.", MessageType.None);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(backendBusy))
            {
                if (GUILayout.Button(new GUIContent("✨ Generate scripting", "Stop ids, goals, playlist Lua and the Dreamie script from the Game Graph; keeps what is authored"), GUILayout.Width(200f), GUILayout.Height(22f))) GenerateScripting(p, false);
                if (GUILayout.Button(new GUIContent("Regenerate (replace)", "Replace the Lua, Dreamie script and inline goals with generated ones"), GUILayout.Width(150f), GUILayout.Height(22f))
                    && EditorUtility.DisplayDialog("Regenerate scripting", "Replace this playlist's Lua, Dreamie script and inline goal blocks?", "Replace", "Cancel")) GenerateScripting(p, true);
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.Label("Stops", EditorStyles.boldLabel);
            foreach (PlaylistLeg leg in p.arc)
            {
                GUILayout.Label($"{leg.name}  ({leg.beat}, {leg.minutes} min)", EditorStyles.miniBoldLabel);
                foreach (PlaylistStop stop in leg.stops)
                {
                    PlaylistLibraryItem item = S.Item(stop.attraction);
                    string kind = !string.IsNullOrEmpty(stop.challenge) ? stop.challenge : PlaylistChallenges.Derive(stop.role, leg.objective?.type, item?.defaultChallenge);
                    string leaf = item?.leaf ?? stop.attraction;
                    JSONObject goal = PlaylistStudioJson.Extra(stop, "goal");
                    bool inline = goal != null && goal.type == JSONObject.Type.Object && goal.GetField("lua") != null;
                    string key = PlaylistGoalStore.Key(leaf, kind);
                    bool hasDefault = S.goals.ContainsKey(key);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label($"  {(string.IsNullOrEmpty(stop.id) ? "(no id)" : stop.id)} · {(string.IsNullOrEmpty(stop.displayName) ? leaf : stop.displayName)} · {kind}", GUILayout.Width(340f));
                    GUILayout.Label(inline ? "inline block" : hasDefault ? "default: " + key : "default (missing: " + key + ")", EditorStyles.miniLabel, GUILayout.Width(250f));
                    using (new EditorGUI.DisabledScope(!inline && !hasDefault))
                        if (GUILayout.Button("Test", EditorStyles.miniButton, GUILayout.Width(40f)))
                        { TestGoal("stop:" + stop.id, inline ? goal.GetField("lua").stringValue : S.goals[key], kind, leaf); GUIUtility.ExitGUI(); }
                    if (inline && GUILayout.Button(new GUIContent("Use default", "Drop the inline block: the title default for its kind runs"), EditorStyles.miniButton, GUILayout.Width(80f)))
                    { PlaylistStudioJson.SetExtra(stop, "goal", JSONObject.CreateStringObject("default")); Changed(); GUIUtility.ExitGUI(); }
                    if (!inline && hasDefault && GUILayout.Button(new GUIContent("Make inline", "Copy the default block into this stop to change it for this playlist only"), EditorStyles.miniButton, GUILayout.Width(80f)))
                    { var g = JSONObject.emptyObject; g.AddField("lua", S.goals[key]); PlaylistStudioJson.SetExtra(stop, "goal", g); Changed(); GUIUtility.ExitGUI(); }
                    EditorGUILayout.EndHorizontal();
                    if (goalTestKey == "stop:" + stop.id && goalTest != null) DrawTestResult(goalTest);
                }
            }

            GUILayout.Space(6f);
            GUILayout.Label("Playlist Lua (runs on the leader)", EditorStyles.boldLabel);
            scriptLuaText = EditorGUILayout.TextArea(scriptLuaText ?? "", MonoText(), GUILayout.MinHeight(140f));
            GUILayout.Label("Dreamie script (JSON: story, beats, react, avoid — never fixed counts)", EditorStyles.boldLabel);
            scriptDreamieText = EditorGUILayout.TextArea(scriptDreamieText ?? "", MonoText(), GUILayout.MinHeight(120f));
            GUILayout.Label("Timing caps (JSON: {totalMin, rounds: {legId: min}}; optional)", EditorStyles.boldLabel);
            scriptTimingText = EditorGUILayout.TextArea(scriptTimingText ?? "", MonoText(), GUILayout.MinHeight(36f));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply", GUILayout.Width(80f))) ApplyScriptBuffers(p);
            if (GUILayout.Button("Revert", GUILayout.Width(80f))) LoadScriptBuffers(p);
            var extras = PlaylistStudioJson.ExtraKeys(p);
            GUILayout.Label(extras.Count > 0 ? "Kept on save: " + string.Join(", ", extras) : "", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
        }

        void LoadScriptBuffers(PackagePlaylist p)
        {
            scriptFor = p.id;
            JSONObject lua = PlaylistStudioJson.Extra(p, "lua"), dreamie = PlaylistStudioJson.Extra(p, "dreamie"), timing = PlaylistStudioJson.Extra(p, "timing");
            scriptLuaText = lua != null && lua.type == JSONObject.Type.String ? lua.stringValue : "";
            scriptDreamieText = dreamie != null && dreamie.type == JSONObject.Type.Object ? PlaylistStudioJson.Pretty(dreamie) : "";
            scriptTimingText = timing != null && timing.type == JSONObject.Type.Object ? timing.Print() : "";
        }

        void ApplyScriptBuffers(PackagePlaylist p)
        {
            if (!string.IsNullOrWhiteSpace(scriptLuaText))
            {
                string err = PlaylistGoalTest.Compile(scriptLuaText, p.id);
                if (err != null) { status = "Playlist Lua not applied: " + err; return; }
            }
            JSONObject dreamie = null, timing = null;
            if (!string.IsNullOrWhiteSpace(scriptDreamieText))
            {
                try { dreamie = new JSONObject(scriptDreamieText); } catch { dreamie = null; }
                if (dreamie == null || dreamie.type != JSONObject.Type.Object) { status = "Dreamie script is not a JSON object."; return; }
            }
            if (!string.IsNullOrWhiteSpace(scriptTimingText))
            {
                try { timing = new JSONObject(scriptTimingText); } catch { timing = null; }
                if (timing == null || timing.type != JSONObject.Type.Object) { status = "Timing is not a JSON object."; return; }
            }
            PlaylistStudioJson.SetExtra(p, "lua", string.IsNullOrWhiteSpace(scriptLuaText) ? null : JSONObject.CreateStringObject(scriptLuaText));
            PlaylistStudioJson.SetExtra(p, "dreamie", dreamie);
            PlaylistStudioJson.SetExtra(p, "timing", timing);
            status = "Scripting applied to the working copy (Save writes it).";
            Changed();
        }

        void GenerateScripting(PackagePlaylist p, bool force)
        {
            backendBusy = true;
            status = "Generating scripting for '" + p.name + "'…";
            PlaylistStudioBackend.Script(contentId, PlaylistStudioJson.WritePlaylist(p, contentId), force, (ok, body, msg) =>
            {
                backendBusy = false;
                if (!ok) { status = "Scripting: " + msg; Repaint(); return; }
                JSONObject g = body.GetField("playlist");
                ApplyGenerated(p, g, force);
                // the library blocks it needs that this title does not have yet
                JSONObject goals = body.GetField("goals");
                int added = 0;
                for (int i = 0; i < (goals?.count ?? 0); i++)
                    if (!S.goals.ContainsKey(goals.keys[i]) && goals.list[i].GetField("lua")?.stringValue is string lua) { S.SetGoal(goals.keys[i], lua); added++; }
                LoadScriptBuffers(p);
                var warns = body.GetField("warnings")?.list?.Select(w => w.stringValue).Where(w => w != null).Take(2).ToList() ?? new List<string>();
                status = $"Scripting generated for '{p.name}'{(added > 0 ? $" (+{added} default goal block(s))" : "")}. Review, Test, then Save." + (warns.Count > 0 ? " " + string.Join("; ", warns) : "");
                Changed();
            });
        }

        /// Copy the generated stop ids + goals, Lua and Dreamie script onto the model (stops matched by position).
        void ApplyGenerated(PackagePlaylist p, JSONObject g, bool force)
        {
            if (g == null) return;
            var legs = g.GetField("arc")?.list ?? new List<JSONObject>();
            for (int i = 0; i < p.arc.Count && i < legs.Count; i++)
            {
                var stops = legs[i].GetField("stops")?.list ?? new List<JSONObject>();
                for (int j = 0; j < p.arc[i].stops.Count && j < stops.Count; j++)
                {
                    PlaylistStop stop = p.arc[i].stops[j];
                    if (string.IsNullOrEmpty(stop.id)) stop.id = stops[j].GetField("id")?.stringValue;
                    JSONObject goal = stops[j].GetField("goal");
                    if (goal != null && (force || PlaylistStudioJson.Extra(stop, "goal") == null)) PlaylistStudioJson.SetExtra(stop, "goal", goal);
                }
            }
            foreach (string key in new[] { "lua", "dreamie" })
            {
                JSONObject v = g.GetField(key);
                if (v != null && (force || PlaylistStudioJson.Extra(p, key) == null)) PlaylistStudioJson.SetExtra(p, key, v);
            }
            if (PlaylistStudioJson.Extra(p, "generatedBy") == null && g.GetField("generatedBy") != null) PlaylistStudioJson.SetExtra(p, "generatedBy", g.GetField("generatedBy"));
        }

        // ───────────────────────────── campaign tab: Lua flags + locks ─────────────────────────────

        /// "Lua sets x, y · locks a (until b)" for a campaign card, "" when the playlist has no Lua.
        static string LuaCampaignLine(PackagePlaylist p)
        {
            JSONObject lua = PlaylistStudioJson.Extra(p, "lua");
            if (lua == null || lua.type != JSONObject.Type.String) return "";
            var flags = PlaylistGoalStore.LuaFlags(lua.stringValue);
            var badges = PlaylistGoalStore.LuaBadges(lua.stringValue);
            var locks = PlaylistGoalStore.LuaLocks(lua.stringValue);
            var parts = new List<string>();
            if (flags.Count > 0) parts.Add("Lua flags " + string.Join(", ", flags));
            if (badges.Count > 0) parts.Add("badges " + string.Join(", ", badges));
            if (locks.Count > 0) parts.Add("locks " + string.Join(", ", locks.Select(l => l.stop + (l.needs.Count > 0 ? " until " + string.Join("+", l.needs) : ""))));
            return string.Join(" · ", parts);
        }

        void DrawCampaignLuaPanel()
        {
            var rows = S.data.playlists.Select(p => (p, line: LuaCampaignLine(p), authored: p.arc.SelectMany(l => l.stops).Where(s => s.lockedUntil != null && s.lockedUntil.Count > 0).ToList()))
                .Where(r => r.line.Length > 0 || r.authored.Count > 0).ToList();
            if (S.data.flagsJson?.Length > 2 || !string.IsNullOrEmpty(S.data.campaignsJson) && S.data.campaignsJson.Length > 2)
                GUILayout.Label($"Title block: {(PlaylistGates.SafeParse(S.data.campaignsJson)?.count ?? 0)} campaign(s), {(PlaylistGates.SafeParse(S.data.flagsJson)?.count ?? 0)} declared flag(s) (kept on save).", EditorStyles.miniLabel);
            if (rows.Count == 0) { GUILayout.Label("No playlist Lua sets flags or locks yet (Script tab → Generate scripting).", EditorStyles.miniLabel); return; }
            GUILayout.Label("Set by playlist Lua (leader, synced to every device)", EditorStyles.boldLabel);
            foreach (var r in rows)
            {
                string authored = r.authored.Count > 0 ? "authored locks " + string.Join(", ", r.authored.Select(s => (string.IsNullOrEmpty(s.id) ? (string.IsNullOrEmpty(s.displayName) ? s.attraction : s.displayName) : s.id) + " until " + string.Join("+", s.lockedUntil))) : "";
                GUILayout.Label($"• {r.p.name}: " + string.Join(" · ", new[] { r.line, authored }.Where(x => x.Length > 0)), EditorStyles.wordWrappedMiniLabel);
            }
        }
    }
}
#endif
