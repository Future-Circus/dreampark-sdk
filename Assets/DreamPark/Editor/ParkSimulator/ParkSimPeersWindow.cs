using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    /// DreamPark → Multiplayer → Simulated Peers: add, move, crash and puppet
    /// simulated players in the running game, and run the standard scenarios.
    public sealed class SimPeersWindow : EditorWindow
    {
        Vector2 _scroll;
        string _sendPeer = "B", _sendTarget = "", _sendKind = "", _sendBody = "{}";
        int _scenario;
        double _nextRepaint;

        static string PrefKey(string n) => "DreamPark.SimPeers.Window." + n + "." + Application.dataPath.GetHashCode();
        static string ParamsJson
        {
            get => EditorPrefs.GetString(PrefKey("Params"), "{\n  \"target\": \"\",\n  \"start\": \"\"\n}");
            set => EditorPrefs.SetString(PrefKey("Params"), value);
        }

        [MenuItem("DreamPark/Multiplayer/Simulated Peers...", false, 40)]
        public static void Open() => GetWindow<SimPeersWindow>("Simulated Peers").Show();

        void Update()
        {
            if (EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 0.5;
            Repaint();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.HelpBox(
                "Simulated players join the Editor's game over the REAL relay (no echo, 60 msg/s per peer, ReliableOrdered) " +
                "and stream real presence, so dp.peers(), name tags, RemoteRigs and the session leader react to them. " +
                "They are wire-level players: the Editor runs the one real copy of the game. See MULTIPLAYER.md § Testing.",
                MessageType.None);

            SimPeersSettings.AutoPeers = EditorGUILayout.IntSlider(new GUIContent("Peers on Play", "Simulated peers added automatically each time Play starts."), SimPeersSettings.AutoPeers, 0, 8);

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play mode to add peers.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            // ── Session ──
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Session", EditorStyles.boldLabel);
            var c = DreamBoxClient.Instance;
            var pp = PlayerPresence.Instance;
            string leader = SimPeerScenario.LeaderId();
            EditorGUILayout.LabelField("Relay", SimPeers.Active ? $"127.0.0.1:{SimPeers.Relay?.Port}  ·  {SimPeers.Relay?.PeerCount} connected  ·  {SimPeers.Relay?.RateLimitedMessageCount} dropped" : "off");
            EditorGUILayout.LabelField("Editor", $"{PlayerPresence.MyId}  ·  {(SimPeers.LocalConnected ? "connected" : "not connected")}  ·  {(c != null ? c.SendRate : 0)} msg/s  ·  sees {(pp != null ? pp.PeerCount : 0)} peer(s)");
            EditorGUILayout.LabelField("Leader", leader == null ? "(no DreamParkSession in this SDK)" : leader == PlayerPresence.MyId ? leader + "  (the Editor)" : leader + "  (" + (SimPeers.All.LastOrDefault(p => p.Id == leader)?.Name ?? "?") + ")");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!SimPeers.Active && GUILayout.Button("Start session")) Report(SimPeers.Start());
                if (GUILayout.Button("+ Peer")) Add(SimPeerRank.High, "headset");
                if (GUILayout.Button(new GUIContent("+ Leader peer", "A peer whose id sorts below the Editor's, so it leads the room."))) Add(SimPeerRank.Low, "headset");
                if (GUILayout.Button("+ Phone")) Add(SimPeerRank.High, "phone");
                if (SimPeers.Active && GUILayout.Button("Stop all")) SimPeers.Stop();
            }

            // ── Peers ──
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Peers", EditorStyles.boldLabel);
            foreach (var p in SimPeers.All.ToArray())
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    string state = p.Gone ? p.GoneHow : !p.Connected ? "connecting" : !p.LinkUp ? "LINK DOWN" : "live";
                    EditorGUILayout.LabelField($"{p.Name}  {p.Id}  {p.Device}  {state}", GUILayout.Width(250));
                    EditorGUILayout.LabelField($"sent {p.SentCount} · recv {p.ReceivedCount} · {(p.Sees(PlayerPresence.MyId) ? "hears Editor" : "—")}", GUILayout.Width(200));
                    using (new EditorGUI.DisabledScope(p.Gone))
                    {
                        if (GUILayout.Button("Come here", GUILayout.Width(80))) p.WalkTo(SimPeers.ResolveSpot("local", null, SimPeers.Live.ToList().IndexOf(p), out _), 1.2f);
                        if (GUILayout.Button(p.LinkUp ? "Drop link" : "Link up", GUILayout.Width(75))) p.LinkUp = !p.LinkUp;
                        if (GUILayout.Button("Quit", GUILayout.Width(45))) SimPeers.Remove(p.Name, "quit", out _);
                        if (GUILayout.Button("Crash", GUILayout.Width(50))) SimPeers.Remove(p.Name, "crash", out _);
                    }
                }
            }

            // ── Send ──
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Send as a peer (= that player's net_send)", EditorStyles.boldLabel);
            _sendPeer = EditorGUILayout.TextField("Peer", _sendPeer);
            _sendTarget = EditorGUILayout.TextField(new GUIContent("Target", "Object path with the NetId (or a NetId number). Empty = a session-level message."), _sendTarget);
            _sendKind = EditorGUILayout.TextField("Kind", _sendKind);
            _sendBody = EditorGUILayout.TextField("Body (JSON)", _sendBody);
            if (GUILayout.Button("Send"))
            {
                var step = new JObject { ["do"] = "peer_send", ["name"] = _sendPeer, ["target"] = _sendTarget, ["kind"] = _sendKind, ["raw"] = _sendBody };
                Report(SimPeerScenario.DoOnce(step, out _));
            }

            // ── Scenarios ──
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scenarios", EditorStyles.boldLabel);
            var names = SimPeerBuiltins.Order;
            _scenario = EditorGUILayout.Popup("Scenario", _scenario, names);
            EditorGUILayout.LabelField("Params (JSON — fills the scenario's ${…} hooks)");
            ParamsJson = EditorGUILayout.TextArea(ParamsJson, GUILayout.MinHeight(60));
            var cur = SimPeerScenario.Current;
            using (new EditorGUI.DisabledScope(cur != null && cur.Status == SimPeerScenario.State.Running))
            {
                if (GUILayout.Button("Run " + names[_scenario]))
                {
                    try
                    {
                        var s = SimPeerScenario.Load(names[_scenario], JObject.Parse(ParamsJson), out string err);
                        if (s == null) Report(err);
                        else { SimPeers.RemoveAll(); Report(SimPeerScenario.Run(s)); }
                    }
                    catch (Exception e) { Report("params: " + e.Message); }
                }
            }
            if (cur != null)
            {
                var sum = cur.Summary();
                EditorGUILayout.LabelField($"{cur.Name}: {cur.Status}  ·  {cur.Passed} passed, {cur.FailedCount} failed, {cur.Skipped} skipped");
                if (cur.Status == SimPeerScenario.State.Running && sum["waiting_on"] != null)
                    EditorGUILayout.LabelField("waiting on", (string)sum["waiting_on"], EditorStyles.wordWrappedMiniLabel);
                foreach (var r in (JArray)sum["results"])
                    EditorGUILayout.LabelField($"{((string)r["result"]).ToUpperInvariant(),-4}  {(string)r["what"]}", (string)r["detail"], EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty((string)sum["log"]) && cur.Status != SimPeerScenario.State.Running && GUILayout.Button("Reveal log"))
                    EditorUtility.RevealInFinder((string)sum["log"]);
            }
            EditorGUILayout.EndScrollView();
        }

        static void Add(SimPeerRank rank, string device)
        {
            SimPeers.Add(new SimPeerOptions { rank = rank, device = device }, out string err);
            Report(err);
        }

        static void Report(string err)
        {
            if (!string.IsNullOrEmpty(err)) Debug.LogWarning(SimPeers.Tag + " " + err);
        }
    }
}
