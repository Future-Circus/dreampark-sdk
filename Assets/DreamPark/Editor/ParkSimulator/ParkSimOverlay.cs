// ─────────────────────────────────────────────────────────────────────
//  ParkSimOverlay.cs — the panel you drive the simulation from
//
//  A Scene view Overlay rather than a floating window, because Unity's
//  Overlay system only supports windows that implement ISupportsOverlays
//  and the Game view does not — GameView is also internal, so it cannot
//  even be named in an [Overlay] attribute. The Scene view is the one
//  surface where this can be a real dockable, collapsible, position-
//  remembering panel instead of a window that fights the layout, and the
//  Alt+Shift+R shortcut covers the case where the Game view has focus.
//
//  STOP IS A BUTTON, NOT A SETTING. The park is what Play does now. A
//  creator who wants their bare scene back presses Stop and gets it back
//  exactly as it was — and then has to press it again on the next Play,
//  because the habit we are trying to build is working against the park
//  layout rather than against an empty origin. Making it a saved
//  preference would let that habit lapse in one click, permanently.
//
//  REGENERATE RESHUFFLES, it does not just rebuild. Every press draws a
//  new seed, so attractions land on different markers, at different
//  rotations, on different grades, next to different neighbours. That
//  churn is the point: a world-space assumption survives one arrangement
//  and dies on the next. With a real park loaded it reloads that park and
//  re-places your content in it, which is the same idea one level up.
//
//  THE PANEL SAYS WHICH PARK THIS IS. Once the park can come from
//  somewhere other than park.fbx, "32 placed · seed 118" stops being
//  enough to know what you are looking at — and a real park that failed to
//  load looks exactly like an empty one unless something says so.
//
//  GO and PATROL exist because culling bugs are invisible from a
//  standstill. Both just move the Scene view — see ParkSimCamera.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace DreamPark.ParkSim
{
    [Overlay(typeof(SceneView), OverlayId, "DreamPark Park Sim", true)]
    public class ParkSimOverlay : Overlay
    {
        public const string OverlayId = "dreampark-park-sim";

        /// DreamPark brand purple, #7700FF.
        private static readonly Color Brand = new Color(0x77 / 255f, 0f, 1f);
        private static readonly Color BrandDim = new Color(0x77 / 255f, 0f, 1f, 0.35f);
        private static readonly Color Caution = new Color(1f, 0.78f, 0.05f);
        private static readonly Color Danger = new Color(1f, 0.36f, 0.30f);
        private static readonly Color Muted = new Color(0.62f, 0.62f, 0.62f);

        private const string LogoPath = "Assets/DreamPark/Textures/dreampark-alt-dp.png";

        private Label _park;
        private Label _status;
        private Label _framed;
        private Button _regenerate;
        private Button _patrol;
        private Button _stop;
        private ScrollView _list;
        private VisualElement _notes;
        private ParkSimReport _shown;
        private int _shownItemCount = -1;
        private int _shownExternalCount = -1;
        private bool _shownStopped;

        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement();
            root.style.minWidth = 274;
            root.style.paddingTop = 4;
            root.style.paddingBottom = 4;

            root.Add(BuildHeader());

            _park = new Label();
            _park.style.marginBottom = 2;
            _park.style.whiteSpace = WhiteSpace.Normal;
            _park.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(_park);

            _status = new Label();
            _status.style.marginBottom = 4;
            _status.style.whiteSpace = WhiteSpace.Normal;
            root.Add(_status);

            _framed = new Label();
            _framed.style.marginBottom = 6;
            _framed.style.fontSize = 10;
            _framed.style.color = Muted;
            _framed.style.whiteSpace = WhiteSpace.Normal;
            root.Add(_framed);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.marginBottom = 4;

            _regenerate = new Button(ParkSimulator.Regenerate) { text = "Regenerate Park" };
            _regenerate.tooltip = "Reshuffle every spawn point and rebuild the park from scratch (Alt+Shift+R).";
            _regenerate.style.flexGrow = 1;
            _regenerate.style.backgroundColor = Brand;
            _regenerate.style.color = Color.white;
            buttons.Add(_regenerate);

            _patrol = new Button(TogglePatrol) { text = "Patrol" };
            _patrol.tooltip = "Walk a loop through every attraction so OptimizedAF actually parks and " +
                              "recovers things. Culling bugs do not show up standing still.";
            _patrol.style.marginLeft = 4;
            buttons.Add(_patrol);

            root.Add(buttons);

            _stop = new Button(ToggleRun);
            _stop.style.marginBottom = 6;
            root.Add(_stop);

            _list = new ScrollView();
            _list.style.maxHeight = 240;
            root.Add(_list);

            _notes = new VisualElement();
            _notes.style.marginTop = 4;
            root.Add(_notes);

            // Overlays get no per-frame callback and the simulation runs
            // asynchronously, so poll rather than trying to push updates from a
            // coroutine across the editor/runtime boundary.
            root.schedule.Execute(Refresh).Every(300);
            Refresh();

            return root;
        }

        private VisualElement BuildHeader()
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 6;

            var logo = AssetDatabase.LoadAssetAtPath<Texture2D>(LogoPath);
            if (logo != null)
            {
                var img = new Image { image = logo, scaleMode = ScaleMode.ScaleToFit };
                img.style.width = 18;
                img.style.height = 18;
                img.style.marginRight = 6;
                header.Add(img);
            }

            var title = new Label("Park Sim");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Brand;
            title.style.flexGrow = 1;
            header.Add(title);

            var rule = new VisualElement();
            rule.style.height = 2;
            rule.style.backgroundColor = BrandDim;
            rule.style.marginTop = 2;

            var wrap = new VisualElement();
            wrap.Add(header);
            wrap.Add(rule);
            wrap.style.marginBottom = 6;
            return wrap;
        }

        private void Refresh()
        {
            if (_status == null) return;

            bool playing = Application.isPlaying;
            bool stopped = ParkSimulator.Stopped;

            _regenerate.SetEnabled(playing && !stopped && !ParkSimulator.IsGenerating);
            _patrol.SetEnabled(playing && !stopped && ParkSimulator.HasPark);
            _stop.SetEnabled(playing && !ParkSimulator.IsGenerating);

            _stop.text = stopped ? "Start Park Sim" : "Stop Park Sim";
            _stop.tooltip = stopped
                ? "Rebuild the park without leaving Play mode."
                : "Tear the park down and put your scene back exactly as it was. " +
                  "You will need to press this again next time you press Play.";
            _stop.style.color = stopped ? Brand : Color.white;
            _stop.style.backgroundColor = stopped ? new Color(0, 0, 0, 0) : BrandDim;

            SetParkLabel();

            if (!playing) {
                _status.text = ParkSimSettings.Enabled
                    ? "Press Play — your attraction will be placed in a full park."
                    : "Park Simulator is off. Turn it on under DreamPark > Park Simulator.";
                _status.style.color = Muted;
                _framed.text = "";
                ClearList();
                return;
            }

            if (stopped) {
                _status.text = "Park simulation stopped. Your scene is back as it was.";
                _status.style.color = Muted;
                _framed.text = "Pressing Play again returns you to the park — that is deliberate.";
                ClearList();
                return;
            }

            if (ParkSimulator.IsGenerating) {
                _status.text = "Building park…";
                _status.style.color = Muted;
                return;
            }

            var report = ParkSimulator.Report;
            if (report == null) {
                _status.text = "No park generated this session.";
                _status.style.color = Muted;
                ClearList();
                return;
            }

            // A park that failed to load is not a park with nothing in it, and
            // an overlay that showed "0 placed" for both would send you looking
            // at your content for a problem that is in the load.
            if (!string.IsNullOrEmpty(report.sourceFailure)) {
                _status.text = report.sourceFailure;
                _status.style.color = Danger;
                _framed.text = "Regenerate to try again, or Stop Park Sim to get your scene back.";
                ClearList();
                return;
            }

            int dirty = 0;
            foreach (var i in report.items) if (i.fromUnappliedOverrides) dirty++;

            int owned = 0;
            foreach (var i in report.items) if (i.simulatorOwned) owned++;

            _status.text = string.Format(
                "{0}{1} · seed {2} · {3:F0}ms{4}",
                report.items.Count + " object" + (report.items.Count == 1 ? "" : "s"),
                owned != report.items.Count ? " (" + owned + " placed by Park Sim)" : "",
                report.seed, report.generateMilliseconds,
                string.IsNullOrEmpty(report.playerName) ? "\nNo player rig — globals are absent." : "");
            _status.style.color = string.IsNullOrEmpty(report.playerName) ? Caution : Muted;

            var framedOn = ParkSimulator.FramedOn;
            _framed.text = framedOn != null
                ? "Framed on " + framedOn.name + ", same offset you had before Play."
                : "";

            _patrol.text = ParkSimCamera.IsPatrolling ? "Stop" : "Patrol";

            // Rebuilding the list every 300ms would fight scrolling and
            // selection, so only when the contents actually changed. External
            // count is in the key because removing a ticket is a change the
            // report's own identity cannot see until the next generation.
            if (ReferenceEquals(_shown, report) && _shownItemCount == report.items.Count
                && _shownExternalCount == ParkSimExternalContent.Count
                && _shownStopped == stopped) return;
            _shown = report;
            _shownItemCount = report.items.Count;
            _shownExternalCount = ParkSimExternalContent.Count;
            _shownStopped = stopped;

            BuildList(report, dirty);
        }

        /// The park's identity line. Blank for the synthetic park, because
        /// "park.fbx" is the only park an SDK project has ever had and naming
        /// it would be noise; a real park's name is the single most useful
        /// thing on the panel.
        private void SetParkLabel()
        {
            var report = ParkSimulator.Report;
            var source = ParkSimulator.ParkSource;

            string name = report != null && !string.IsNullOrEmpty(report.parkName)
                ? report.parkName
                : (source != null ? source.DisplayName : null);

            string detail = report != null && !string.IsNullOrEmpty(report.parkDetail)
                ? report.parkDetail
                : (source != null ? source.Detail : null);

            if (string.IsNullOrEmpty(name)) {
                _park.text = "";
                _park.style.display = DisplayStyle.None;
                return;
            }

            _park.style.display = DisplayStyle.Flex;
            _park.text = string.IsNullOrEmpty(detail) ? name : name + "\n" + detail;
            _park.style.color = Brand;
        }

        private void BuildList(ParkSimReport report, int dirty)
        {
            _list.Clear();
            _notes.Clear();

            foreach (var item in report.items) {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 2;

                var go = new Button(() => GoTo(item)) { text = "Go" };
                go.tooltip = "Move the guest to this attraction and look at it.";
                go.style.width = 34;
                go.style.marginRight = 4;
                row.Add(go);

                string suffix = item.fromSample ? "  (Sample)"
                              : item.external ? "  (" + (item.externalOrigin ?? "injected") + ")"
                              : !item.simulatorOwned ? "  (in the park)"
                              : "";

                var label = new Label(item.name + suffix);
                label.style.flexGrow = 1;
                label.style.overflow = Overflow.Hidden;
                if (item.fromUnappliedOverrides) label.style.color = Caution;
                else if (ReferenceEquals(item, ParkSimulator.FramedOn)) label.style.color = Brand;
                else if (!item.simulatorOwned) label.style.color = Muted;
                label.tooltip = BuildTooltip(item);
                row.Add(label);

                if (item.fromUnappliedOverrides) {
                    var badge = new Label("!");
                    badge.style.color = Caution;
                    badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                    badge.style.marginLeft = 4;
                    badge.style.width = 12;
                    row.Add(badge);
                }

                // Injected content is the only thing on this list that can be
                // taken back out, because it is the only thing that got here by
                // somebody asking for it rather than by being in the project.
                if (item.external && !string.IsNullOrEmpty(item.externalId)) {
                    string id = item.externalId;
                    var drop = new Button(() => RemoveExternal(id)) { text = "✕" };
                    drop.tooltip = "Take this out of the park. It goes back the moment you tap it again.";
                    drop.style.width = 20;
                    drop.style.marginLeft = 4;
                    row.Add(drop);
                }

                _list.Add(row);
            }

            if (dirty > 0) {
                AddNote(string.Format(
                    "{0} attraction{1} spawned with unapplied scene changes. What you are testing is not " +
                    "what the prefab contains — apply them before you trust the result.",
                    dirty, dirty == 1 ? "" : "s"), Caution);
            }

            foreach (var note in report.notes) AddNote(note, Caution);
        }

        private static string BuildTooltip(PlacedItem item)
        {
            if (!item.simulatorOwned) {
                return item.name +
                       "\n\nPart of the park itself, loaded through the shipping path. " +
                       "Park Sim did not place it and does not touch its floor.";
            }

            return string.Format(
                "{0}{4}{5}\nmarker: {1}\nfloor: {2}{3}",
                item.name, item.marker,
                item.floorReplayed ? "replayed from cached calibration (load path)"
                                   : "baked on placement (ConformOnce)",
                item.fromUnappliedOverrides
                    ? "\n\nSpawned from a scene instance with unapplied overrides — this is NOT what the prefab contains."
                    : "",
                item.fromSample ? "\nfrom the bundled Sample project, not your content" : "",
                item.external ? "\ninjected from " + (item.externalOrigin ?? "a host tool") +
                                " — pinned, so it is in every generation" : "");
        }

        private void AddNote(string text, Color color)
        {
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = color;
            label.style.fontSize = 10;
            label.style.marginTop = 3;
            _notes.Add(label);
        }

        private void ClearList()
        {
            _shown = null;
            _shownItemCount = -1;
            _shownExternalCount = -1;
            if (_list != null) _list.Clear();
            if (_notes != null) _notes.Clear();
        }

        private static void RemoveExternal(string id)
        {
            if (!ParkSimExternalContent.Remove(id)) return;
            // Rebuild rather than deleting the instance in place: the park is
            // laid out around what is in it, and leaving a hole where an
            // attraction was would misrepresent the arrangement the rest of the
            // park was calibrated and gap-filled for.
            ParkSimulator.RegenerateWhenIdle();
        }

        private static void GoTo(PlacedItem item)
        {
            if (item.instance == null) return;
            ParkSimCamera.TeleportTo(item.instance.position);
        }

        private static void ToggleRun()
        {
            if (ParkSimulator.Stopped) ParkSimulator.Start();
            else ParkSimulator.Stop();
        }

        private static void TogglePatrol()
        {
            var report = ParkSimulator.Report;
            if (report == null) return;

            bool turningOn = !ParkSimCamera.IsPatrolling;

            var route = new List<Vector3>();
            foreach (var item in report.items) {
                if (item.instance != null) route.Add(item.instance.position);
            }

            ParkSimCamera.SetPatrol(turningOn, route);
        }

        [Shortcut("DreamPark/Park Simulator/Regenerate Park", KeyCode.R,
                  ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void RegenerateShortcut()
        {
            ParkSimulator.Regenerate();
        }
    }
}
