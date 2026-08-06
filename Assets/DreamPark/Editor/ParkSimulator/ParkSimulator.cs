// ─────────────────────────────────────────────────────────────────────
//  ParkSimulator.cs — a real dreampark-core park, built in the SDK
//
//  THE PROBLEM. An attraction that behaves perfectly in the developer's
//  scene is sitting at the origin, unrotated, on flat ground, alone. In an
//  actual park it is one of a dozen attractions, rotated to whatever angle
//  the operator placed it at, on a floor that follows the terrain, with
//  OptimizedAF parking and unparking it as guests come and go. Every one of
//  those differences is a place for a world-space-vs-local-space assumption
//  to hide, and today the first time anyone finds out is in a venue.
//
//  WHAT THIS IS NOT. It is not a mock. The hierarchy, the load ordering,
//  the physics release, the calibration and the optimizer are the same code
//  paths core runs — this file only supplies the inputs core would get from
//  the backend and the headset. Where core would resolve an Addressable,
//  this instantiates a prefab; where core would receive a park document,
//  this generates one from the markers in park.fbx. Everything downstream
//  of that is the shipping implementation, which is the whole point: a bug
//  reproduced here is a real bug, and a fix verified here is really fixed.
//
//  WHERE THE PARK COMES FROM IS PLUGGABLE. By default it is park.fbx and
//  its markers. A host app can register a ParkSimParkSource instead — most
//  usefully a REAL park document loaded through the shipping loader — and
//  then the simulator does not build a synthetic venue at all: it adopts
//  the park the source built, and places the creator's own content into it.
//  Loading a real park stopped being a competing feature and became a
//  different source for the same simulation. See ParkSimSource.cs.
//
//  ORDERING IS THE SPECIFICATION. The sequence below mirrors
//  LevelAnchor.LoadLevel exactly, and the order is not incidental:
//
//    BeginParkLoad + Disable   nothing runs while the park assembles, so an
//                              attraction's Start() cannot observe a
//                              half-built park
//    player rig FIRST          globals live on Player.prefab and must bind
//                              before any attraction's Start/Update
//    register startDisabled    spawned content stays parked until the whole
//                              park is up
//    settle one second         mirrors LoadLevel's UniTask.Delay(1000)
//    ApplyPendingCalibration   floors move only while rigidbodies are parked
//    wait floors, wait gaps    LevelTemplate.runtimePlane, then GapFiller
//    Unlock + Release          exactly once, park-wide, at the very end
//
//  THE SIMULATOR ONLY TOUCHES WHAT IT PLACED. With a source in play the
//  scene also contains objects the SOURCE spawned, which have already run
//  that ladder themselves and carry floor data baked against a real venue.
//  Calibrating them again would overwrite a real floor with a guess, so
//  every step above is scoped to the simulator's own sub-tree and every
//  report item records whether the simulator owns it.
//
//  Everything the simulator creates is destroyed on regenerate, and Unity's
//  scene reload discards the rest on exiting Play. Nothing it does to the
//  developer's scene — disabling instances, deleting clean ones — survives
//  the session, so there is nothing here that can lose work.
// ─────────────────────────────────────────────────────────────────────

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Defective.JSON;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DreamPark.ParkSim
{
    /// Marker so teardown can find everything the simulator owns without
    /// keeping references across a domain reload.
    public class ParkSimRoot : MonoBehaviour { }

    /// Coroutine host. Lives on the sim root, dies with it.
    public class ParkSimRunner : MonoBehaviour { }

    public class PlacedItem
    {
        public string name;
        public ContentKind kind;
        public bool fromUnappliedOverrides;
        public bool fromSample;
        public string assetPath;
        public string marker;
        public bool floorReplayed;
        public Transform instance;

        /// The simulator instantiated this and is responsible for its
        /// calibration, its registration and its floor cache. False for
        /// anything a park source spawned — that content already loaded
        /// through the shipping path and must not be touched again.
        public bool simulatorOwned = true;

        /// Injected through ParkSimExternalContent rather than found by the
        /// project scan — published content, a test build, a Content Manager
        /// pick. Carries the ticket id so the overlay can drop it again.
        public bool external;
        public string externalId;
        public string externalOrigin;
    }

    public class ParkSimReport
    {
        public int seed;
        public double generateMilliseconds;
        public string playerName;

        /// What park this is. Null for the synthetic park built from park.fbx.
        public string parkName;
        public string parkDetail;

        /// Set when a park source could not build its park. The overlay shows
        /// this instead of an empty item list, because "the park failed to
        /// load" and "the park is empty" are very different problems.
        public string sourceFailure;

        public readonly List<PlacedItem> items = new List<PlacedItem>();
        public readonly List<string> notes = new List<string>();

        /// Items the simulator placed itself — everything it may calibrate,
        /// cache floors for, or wait on before releasing physics.
        public IEnumerable<PlacedItem> OwnedItems
        {
            get { foreach (var i in items) if (i.simulatorOwned) yield return i; }
        }
    }

    [InitializeOnLoad]
    public static class ParkSimulator
    {
        public static ParkSimReport Report { get; private set; }
        public static bool IsGenerating { get; private set; }
        public static bool HasPark { get { return _root != null; } }

        /// True when the creator pressed Stop this session. Deliberately NOT
        /// persisted: the next Play builds the park again. Seeing your
        /// attraction in a park is the default, and opting out is a decision
        /// you make each time rather than one you make once and forget.
        public static bool Stopped { get; private set; }

        /// The attraction the Scene view was re-framed onto, so the overlay can
        /// say which one you are looking at.
        public static PlacedItem FramedOn { get; private set; }

        /// Where the park comes from. Null means the synthetic park built from
        /// park.fbx, which is what every SDK project gets.
        public static ParkSimParkSource ParkSource { get { return _source; } }

        private static GameObject _root;
        private static GameObject _environment;
        private static ParkSimParkSource _source;
        private static bool _deferAutoGenerate;

        private static readonly List<GameObject> _pendingSuspension = new List<GameObject>();
        private static readonly List<GameObject> _disabledSceneTemplates = new List<GameObject>();

        /// Calibration results keyed by "<content>@<marker>", so an attraction
        /// that lands back on a marker it has already been calibrated against
        /// takes the LOAD path (floorData -> ApplyPendingCalibration) instead of
        /// re-conforming. That is the path a returning guest takes, and it is
        /// otherwise unreachable from the SDK.
        private static readonly Dictionary<string, JSONObject> _floorCache =
            new Dictionary<string, JSONObject>();

        static ParkSimulator()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) {
                // BEFORE the domain reload, while the creator's own scene is
                // still loaded and the Scene view is still framed on whatever
                // they were working on.
                Stopped = false;
                if (ParkSimSettings.Enabled) ParkSimViewpoint.Capture();
            } else if (change == PlayModeStateChange.EnteredPlayMode) {
                if (!ParkSimSettings.Enabled) return;
                if (_deferAutoGenerate) {
                    // A host tool is still assembling what the park should
                    // contain. It owns the first Generate now — see
                    // DeferAutoGenerate.
                    _deferAutoGenerate = false;
                    return;
                }
                Generate(NextSeed());
            } else if (change == PlayModeStateChange.ExitingPlayMode) {
                // Unity's scene reload undoes all of it anyway; clearing the
                // statics just stops a stale report showing in the overlay.
                _root = null;
                _environment = null;
                _pendingSuspension.Clear();
                _disabledSceneTemplates.Clear();
                Report = null;
                FramedOn = null;
                Stopped = false;
                IsGenerating = false;
                // A deferral that was never honoured must not carry into the
                // next play session, where it would silently suppress the park.
                _deferAutoGenerate = false;
                // Cycling state is per play session. The domain reload clears
                // it anyway, but "Enter Play Mode Options" can skip that reload
                // and a bag left mid-cycle would make the next session's first
                // Regenerate look arbitrary.
                ParkSimSelection.Reset();
                // Tickets hold live resolvers over Addressables handles that do
                // not outlive the play session. A host that wants a tap to
                // survive re-entering Play re-adds its own descriptors — it is
                // the only thing that can resolve the prefab again.
                ParkSimExternalContent.Clear();
            }
        }

        private static int NextSeed()
        {
            int pinned = ParkSimSettings.Seed;
            if (pinned != 0) return pinned;
            return Random.Range(1, int.MaxValue);
        }

        /// <summary>
        /// Choose where the park comes from. Pass null to go back to the
        /// synthetic park built from park.fbx.
        ///
        /// Does NOT rebuild on its own: a caller setting a source before
        /// entering play mode wants the normal entry to pick it up, and a
        /// caller swapping sources mid-session decides for itself whether that
        /// is worth a Regenerate.
        /// </summary>
        public static void SetParkSource(ParkSimParkSource source)
        {
            if (ReferenceEquals(_source, source)) return;
            if (_source != null) {
                try { _source.Teardown(); }
                catch (System.Exception e) { Debug.LogException(e); }
            }
            _source = source;
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Skip the automatic generate on the NEXT play-mode entry, because a
        /// host tool is still working out what the park should contain — a
        /// content pick that has to mount a catalog before it can be resolved,
        /// say. One-shot, and it expires whether or not the host uses it.
        ///
        /// A host that calls this MUST call Generate or Regenerate itself,
        /// including on its own failure paths: a deferral nobody honours is a
        /// play session with no park in it, which is worse than a park with
        /// something missing from it.
        ///
        /// Meant to be called from an [InitializeOnLoad] static constructor,
        /// which is the only place ordering against the simulator's own
        /// play-mode handler is guaranteed.
        /// </summary>
        public static void DeferAutoGenerate()
        {
            _deferAutoGenerate = true;
        }

        /// <summary>
        /// Rebuild as soon as the simulator is free, rather than dropping the
        /// request if it happens to be busy.
        ///
        /// Regenerate no-ops while a generation is running, which is correct
        /// for a button a human presses — the overlay greys it out — and wrong
        /// for a tool calling it. A real park's Build can be awaiting the
        /// network for tens of seconds, and that is EXACTLY the window in which
        /// somebody taps a second attraction. Dropping that tap silently is the
        /// bug; waiting a few frames is not.
        ///
        /// Also handles the stopped case, so callers do not each have to
        /// remember that Regenerate warns and does nothing after a Stop.
        /// </summary>
        public static void RegenerateWhenIdle()
        {
            if (!Application.isPlaying) return;

            if (!IsGenerating) {
                if (Stopped) Start(); else Regenerate();
                return;
            }

            // Deadline so a generation that never finishes cannot leave a
            // callback on EditorApplication.update forever.
            double deadline = EditorApplication.timeSinceStartup + 180d;
            EditorApplication.CallbackFunction tick = null;
            tick = () => {
                if (IsGenerating && EditorApplication.timeSinceStartup < deadline) return;
                EditorApplication.update -= tick;
                if (!Application.isPlaying) return;
                if (Stopped) Start(); else Regenerate();
            };
            EditorApplication.update += tick;
        }

        /// <summary>
        /// Tear the park down and build a new one with a fresh spawn-point
        /// shuffle. This is what the overlay's Regenerate button calls.
        /// </summary>
        public static void Regenerate()
        {
            if (!Application.isPlaying) {
                Debug.LogWarning("[ParkSim] Regenerate is only available in Play mode.");
                return;
            }
            if (IsGenerating) return;
            Generate(NextSeed());
        }

        public static void Generate(int seed)
        {
            if (!Application.isPlaying || IsGenerating) return;

            Teardown();

            _root = new GameObject("[ParkSim] Simulated Park");
            _root.AddComponent<ParkSimRoot>();
            var runner = _root.AddComponent<ParkSimRunner>();
            runner.StartCoroutine(GenerateRoutine(seed));
        }

        /// Called by the content triage for clean scene instances, which are
        /// respawned from their asset instead. Queued rather than disabled
        /// inline so the scan can finish walking the scene it is mutating.
        public static void MarkForSuspension(GameObject go)
        {
            if (go != null) _pendingSuspension.Add(go);
        }

        /// <summary>
        /// Tear the park down and give the creator their scene back exactly as
        /// it was. Everything the simulator disabled is re-enabled; everything
        /// it created is destroyed.
        ///
        /// Not a saved preference. The next Play rebuilds the park — pressing
        /// this is a decision about THIS run, because the whole reason the
        /// simulator is on by default is that working against the park layout
        /// has to become the habit rather than the exception.
        /// </summary>
        public static void Stop()
        {
            Teardown();
            RestoreSceneOriginals();
            Report = null;
            FramedOn = null;
            Stopped = true;
            SceneView.RepaintAll();
            Debug.Log("[ParkSim] Park simulation stopped — your scene is back as it was. " +
                      "Press Play again (or Start Park Sim) to return to the park.");
        }

        /// Bring the park back after a Stop, without leaving play mode.
        public static void Start()
        {
            if (!Application.isPlaying) return;
            Stopped = false;
            Generate(NextSeed());
        }

        private static void RestoreSceneOriginals()
        {
            foreach (var go in _disabledSceneTemplates) {
                if (go != null) go.SetActive(true);
            }
            _disabledSceneTemplates.Clear();
        }

        // ── Generation ───────────────────────────────────────────────────

        /// <summary>
        /// Drives the generation by hand so an exception inside it cannot leave
        /// IsGenerating stuck true.
        ///
        /// That matters more than it looks: Regenerate and Generate both no-op
        /// silently while IsGenerating is set, so ONE throw — a bad prefab's
        /// Awake, a park source faulting mid-load — would kill the simulator
        /// for the rest of the play session with no way back except exiting
        /// play mode. Unity's own coroutine runner logs the exception and stops
        /// the coroutine, which means a plain try/finally around a nested
        /// `yield return inner` would never run its finally.
        ///
        /// MoveNext is inside the try and the yield is outside it, which is
        /// what C# requires (CS1626) and is also what makes this work at all.
        /// </summary>
        private static IEnumerator GenerateRoutine(int seed)
        {
            var inner = GenerateRoutineBody(seed);

            while (true) {
                object current = null;
                try {
                    if (!inner.MoveNext()) break;
                    current = inner.Current;
                } catch (System.Exception e) {
                    Debug.LogException(e);
                    var failed = Report;
                    if (failed != null && string.IsNullOrEmpty(failed.sourceFailure)) {
                        failed.sourceFailure = "The park generation threw: " + e.Message +
                                               " — see the console.";
                    }
                    IsGenerating = false;
                    SceneView.RepaintAll();
                    yield break;
                }
                yield return current;
            }

            IsGenerating = false;
        }

        private static IEnumerator GenerateRoutineBody(int seed)
        {
            IsGenerating = true;
            var stopwatch = Stopwatch.StartNew();
            var report = new ParkSimReport { seed = seed };
            Report = report;

            // Domain reload normally clears these, but "Enter Play Mode
            // Options" can be configured to skip it, and a run aborted inside
            // a load leaves the content lock held for up to two minutes. Cheap
            // to reset, expensive to debug. Done BEFORE a source builds, so a
            // source running the real load ladder is never reset out from
            // under itself.
            ParkBuilder.LevelObjectManager.ResetParkLoad();
            ParkBuilder.LevelObjectManager.objectsEnabled = true;

            EnsureOptimizedAF(report.notes);

            var source = _source;
            List<SpawnPoint> spawnPoints;
            bool sourceProvidesGround = true;

            if (source == null) {
                // ── The synthetic park ───────────────────────────────────
                _environment = ParkSimPark.SpawnEnvironment(report.notes);
                if (_environment != null) _environment.transform.SetParent(_root.transform, true);
                spawnPoints = ParkSimPark.CollectSpawnPoints(_environment, seed, report.notes);
            } else {
                // ── Somebody else's park ─────────────────────────────────
                report.parkName = source.DisplayName;
                report.parkDetail = source.Detail;

                var context = new ParkSimSourceContext(_root.transform, seed, report.notes);
                IEnumerator build = null;
                try { build = source.Build(context); }
                catch (System.Exception e) {
                    Debug.LogException(e);
                    context.Fail("The park source threw before it started: " + e.Message);
                }

                // Driven by hand rather than `yield return build`, for the same
                // reason GenerateRoutine drives this method by hand: a nested
                // `yield return <IEnumerator>` is run by Unity's coroutine
                // scheduler, so a throw inside the source is logged by Unity and
                // never reaches us — and the generation would hang with
                // IsGenerating set and an overlay stuck on "Building park…".
                // A source's park is the most likely thing here to fail: it is
                // the only part that talks to a network.
                //
                // MoveNext inside the try, yield outside it (CS1626).
                while (build != null && !context.Failed) {
                    object step = null;
                    try {
                        if (!build.MoveNext()) break;
                        step = build.Current;
                    } catch (System.Exception e) {
                        Debug.LogException(e);
                        context.Fail("The park source threw while building " +
                                     source.DisplayName + ": " + e.Message + " — see the console.");
                        break;
                    }
                    yield return step;
                }

                if (context.Failed) {
                    report.sourceFailure = context.FailureReason;
                    stopwatch.Stop();
                    report.generateMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    IsGenerating = false;
                    Debug.LogError("[ParkSim] " + context.FailureReason);
                    SceneView.RepaintAll();
                    yield break;
                }

                try { source.Describe(report); }
                catch (System.Exception e) { Debug.LogException(e); }
                foreach (var item in report.items) item.simulatorOwned = false;

                spawnPoints = source.CollectSpawnPoints(seed, report.notes) ?? new List<SpawnPoint>();
                sourceProvidesGround = source.ProvidesGroundMesh;

                if (!sourceProvidesGround) {
                    report.notes.Add(
                        "This park has no ARMesh surface to raycast against — its own attractions carry " +
                        "floor data baked at the venue, but anything placed here from your project or the " +
                        "Content Manager cannot conform and will sit flat.");
                }
            }

            var scan = ParkSimContent.Scan(ParkSimSettings.IncludeProps);
            report.notes.AddRange(scan.notes);
            FlushSuspensions();
            DisableSceneTemplates(scan);

            // ── The load ─────────────────────────────────────────────────
            // Refcounted, so nesting this inside a source that has already
            // finished its own load pair is safe: depth returns to zero here
            // and the unlock at the end is the one that matters.
            ParkBuilder.LevelObjectManager.BeginParkLoad();
            if (ParkBuilder.LevelObjectManager.Instance != null)
                ParkBuilder.LevelObjectManager.Instance.Disable();
            OptimizedAF.SignalLoadEvent();

            Transform parkAnchor = NewChild(_root.transform, "parksim-park");
            Transform portalAnchor = NewChild(parkAnchor, "parksim-portal-0");
            // Core creates this inside PortalAnchor.Awake as the single
            // toggleable parent for every level under a portal. Reproduced by
            // name so a hierarchy screenshot from the simulator is comparable
            // to one from a headset.
            Transform subLevelRoot = NewChild(portalAnchor, "SubLevelRoot");

            // Capacity is the marker count — with the synthetic park, park.fbx
            // decides how many places exist, so adding markers raises the
            // ceiling with no code change. With a source it is however many
            // free places that park reported.
            //
            // A source's park is already full of its own attractions, so
            // nothing rotates into it: only content that is PINNED — what was
            // in your scene when you pressed Play, plus anything injected —
            // gets placed. Rotating strangers through a real venue would bury
            // the one thing you are trying to look at.
            var selected = ParkSimSelection.Choose(
                scan.placeables, spawnPoints.Count, seed, report.notes, source != null);

            var placements = AssignPlacements(selected, spawnPoints, report.notes);

            // A source's park usually brings its own player rig. Spawning a
            // second one would bind every global on Player.prefab twice.
            bool playerSpawned = source != null && _root.GetComponentInChildren<PlayerRig>(true) != null;
            if (playerSpawned) {
                report.playerName = "(from " + (report.parkName ?? "the park source") + ")";
            }
            int objectIndex = 0;

            for (int i = 0; i < selected.Count; i++) {
                var entry = selected[i];
                if (entry.Source == null) continue;

                SpawnPoint point = placements[i];

                // One LevelAnchor per placed object, which is how a real park
                // is built: PortalAnchor.NewLevel creates a fresh LevelAnchor
                // every time the operator commits a placement.
                string levelId = "lvl-" + objectIndex + "-" + Sanitize(entry.displayName);
                Transform levelAnchor = NewChild(subLevelRoot, levelId);
                levelAnchor.SetPositionAndRotation(point.position, point.rotation);

                // Player rig FIRST, before any attraction's Start runs. Core
                // does this for the same reason (LevelAnchor.LoadLevel, the
                // "PLAYER RIG FIRST" block): globals live on Player.prefab and
                // anything that binds to them in Start would otherwise miss.
                if (!playerSpawned && scan.player != null && scan.player.Source != null) {
                    var player = InstantiateSource(scan.player);
                    player.name = scan.player.displayName;
                    player.transform.SetParent(levelAnchor, true);
                    player.transform.localPosition = Vector3.zero;
                    Register(player, false);
                    report.playerName = scan.player.displayName;
                    playerSpawned = true;
                }

                var placed = SpawnOne(entry, levelAnchor, levelId, objectIndex, point, report);
                if (placed != null) objectIndex++;

                // Spread the work so a project with many attractions does not
                // stall the editor on one frame.
                if ((i & 3) == 3) yield return null;
            }

            // A project with a Player but no attractions yet still deserves a
            // running park to look at, and the player must never be left
            // unspawned just because the loop above had nothing to iterate.
            if (!playerSpawned && scan.player != null && scan.player.Source != null) {
                Transform soloAnchor = NewChild(subLevelRoot, "lvl-player");
                if (spawnPoints.Count > 0) {
                    soloAnchor.SetPositionAndRotation(spawnPoints[0].position, spawnPoints[0].rotation);
                }
                var player = InstantiateSource(scan.player);
                player.name = scan.player.displayName;
                player.transform.SetParent(soloAnchor, true);
                player.transform.localPosition = Vector3.zero;
                Register(player, false);
                report.playerName = scan.player.displayName;
            }

            // Mirrors LoadLevel's `await UniTask.Delay(1000)`: templates need a
            // frame to build their floors, and calibration must not move a
            // floor out from under a rigidbody that has already woken up.
            yield return new WaitForSeconds(1f);

            // Scoped to what the simulator placed. A source's attractions have
            // already applied their own calibration during their own load, and
            // re-running it would move a real venue floor for no reason.
            foreach (var calibrator in parkAnchor.GetComponentsInChildren<CalibrateLevel>(true)) {
                calibrator.ApplyPendingCalibration();
            }

            yield return CalibrateFresh(report, sourceProvidesGround);

            bool lastOut = ParkBuilder.LevelObjectManager.EndParkLoad();
            if (lastOut) yield return ReleaseParkPhysics(report, parkAnchor);

            CacheFloorData(report);

            stopwatch.Stop();
            report.generateMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            IsGenerating = false;

            // Re-frame the Scene view on the attraction the creator was
            // already looking at, at the same offset — see ParkSimViewpoint.
            // Simulator copies the Scene view onto Camera.main every frame, so
            // moving the view is the whole of "move the guest".
            if (ParkSimSettings.DriveCamera) FramedOn = ParkSimViewpoint.TryApply(report);

            if (Object.FindFirstObjectByType<Simulator>() == null) {
                report.notes.Add(
                    "No Simulator in the scene, so nothing copies the Scene view onto Camera.main. " +
                    "Go and Patrol will still move the Scene view, but the guest will not move and " +
                    "OptimizedAF culling will not be exercised. Add Resources/Prefabs/Simulator.");
            }

            Debug.Log(string.Format(
                "[ParkSim] {0}: {1} object(s) ({2} placed by the simulator, {3} carrying unapplied " +
                "scene changes), seed {4}, {5:F0}ms.",
                report.parkName ?? "Simulated park",
                report.items.Count, CountOwned(report), CountDirty(report), seed,
                report.generateMilliseconds));

            foreach (var note in report.notes) Debug.LogWarning("[ParkSim] " + note);

            SceneView.RepaintAll();
        }


        /// <summary>
        /// Decide where each placeable goes. Attractions take the shuffled
        /// spawn markers; PROPS ARE CLUSTERED BESIDE AN ATTRACTION rather than
        /// given far-flung markers of their own.
        ///
        /// That is not cosmetic, and it was a real flaw in this simulator. A
        /// prop reports the ground beneath it through PropTemplate.SurfaceHeight,
        /// and GapFiller treats every prop as a height contributor exactly like
        /// a floor — with NO distance cutoff, so a prop pulls on the fill
        /// everywhere in the park. Scattering props to independent markers
        /// across park.fbx's ~47m of relief therefore asked GapFiller to
        /// interpolate between a prop correctly sitting on a hilltop and an
        /// attraction correctly sitting in a valley. The smooth inverse-distance
        /// ramp it produced was right for the input and looked like a canyon.
        ///
        /// No operator places content that way. Props go beside the attraction
        /// they belong to, on the same ground, which is what this reproduces.
        /// </summary>
        private static List<SpawnPoint> AssignPlacements(
            List<ContentEntry> items, List<SpawnPoint> markers, List<string> notes)
        {
            var result = new SpawnPoint[items.Count];
            var hosts = new List<SpawnPoint>();
            var origin = new SpawnPoint {
                markerName = "<no marker>", position = Vector3.zero,
                rotation = Quaternion.identity, grounded = false,
            };

            // Pass 1 — attractions claim markers, CLOSEST-FIRST FROM AN ANCHOR.
            //
            // The shuffle already randomised the marker order; taking them in
            // that order scattered attractions across the whole of park.fbx,
            // hundreds of metres apart. That is not what a venue looks like —
            // a real site is tens of metres across — and it has a hard cost:
            // GapFiller tessellates the COMBINED BOUNDS of every floor at
            // verticesPerMeter (3/m) with no size guard, so the vertex count
            // grows with the SQUARE of the spread. 50m of spread is ~23k
            // vertices; 400m is ~1.4 million, and the load times out waiting
            // for ground that is still being built.
            //
            // So the shuffled first marker becomes the venue anchor and the
            // rest are taken nearest-first. The seed still moves the whole
            // venue somewhere new on every regenerate — it just stops the
            // venue being the size of the park.
            var ordered = new List<SpawnPoint>(markers);
            if (ordered.Count > 1) {
                Vector3 anchor = ordered[0].position;
                ordered.Sort((a, b) =>
                    (a.position - anchor).sqrMagnitude.CompareTo((b.position - anchor).sqrMagnitude));
            }

            int cursor = 0;
            for (int i = 0; i < items.Count; i++) {
                if (items[i].kind != ContentKind.Attraction) continue;
                SpawnPoint p = ordered.Count > 0 ? ordered[cursor++ % ordered.Count] : origin;
                result[i] = p;
                hosts.Add(p);
            }

            WarnIfVenueTooLarge(hosts, notes);

            // Pass 2 — props ring the attraction they are assigned to. With no
            // attractions at all there is nothing to cluster around, so they
            // fall back to markers of their own.
            int propIndex = 0;
            for (int i = 0; i < items.Count; i++) {
                if (items[i].kind == ContentKind.Attraction) continue;

                if (hosts.Count == 0) {
                    result[i] = markers.Count > 0 ? markers[cursor++ % markers.Count] : origin;
                    propIndex++;
                    continue;
                }

                SpawnPoint host = hosts[propIndex % hosts.Count];

                // Golden angle, so successive props fan out around the host
                // instead of stacking on one bearing. Deterministic — the seed
                // already varies the layout through the marker shuffle, and a
                // second random source would only make repros harder.
                float angle = propIndex * 137.508f * Mathf.Deg2Rad;
                float radius = 5f + (propIndex % 3) * 2.5f;
                Vector3 candidate = host.position +
                    new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;

                result[i] = new SpawnPoint {
                    markerName = host.markerName + " +prop",
                    // Dropped, not inherited: on a slope the host's elevation a
                    // few metres away is not the ground, and a prop floating
                    // above or buried under it would feed GapFiller exactly the
                    // bad height this whole arrangement exists to avoid. With no
                    // ARMesh in the world the drop is a no-op and returns the
                    // candidate unchanged, which is the right answer there too.
                    position = ParkSimPark.DropToGround(candidate),
                    rotation = host.rotation,
                    grounded = true,
                };
                propIndex++;
            }

            int attractions = hosts.Count;
            if (attractions > 0 && markers.Count > 0 && attractions > markers.Count) {
                notes.Add(attractions + " attractions but only " + markers.Count +
                          " spawn markers — markers were reused, so some overlap.");
            }
            if (attractions > 0 && markers.Count == 0) {
                notes.Add("This park reported no free places to put anything, so " + attractions +
                          " object(s) are stacked at the origin.");
            }
            return new List<SpawnPoint>(result);
        }


        /// <summary>
        /// GapFiller has no upper bound on its own output: it tessellates the
        /// combined bounds of every floor at verticesPerMeter with no guard, so
        /// the cost is quadratic in how far apart the content is. This turns
        /// the resulting stall into a number the developer can act on instead
        /// of a bare "did not finish" further down the load.
        /// </summary>
        private static void WarnIfVenueTooLarge(List<SpawnPoint> hosts, List<string> notes)
        {
            if (hosts.Count < 2) return;

            var b = new Bounds(hosts[0].position, Vector3.zero);
            for (int i = 1; i < hosts.Count; i++) b.Encapsulate(hosts[i].position);

            // Mirrors GapFiller.boundsPadding (3m, expanded both ways) and its
            // default verticesPerMeter, so the estimate matches what it will
            // actually build.
            float w = b.size.x + 12f;
            float h = b.size.z + 12f;
            long verts = (long)(w * 3f + 1f) * (long)(h * 3f + 1f);

            if (verts < 250000) return;

            notes.Add(string.Format(
                "Attractions span {0:F0}m x {1:F0}m — GapFiller will try to build roughly {2:N0} " +
                "vertices to fill between them, which is why the load stalls. Real venues are tens " +
                "of metres across, not hundreds; park.fbx's markers are further apart than that.",
                b.size.x, b.size.z, verts));
        }

        private static PlacedItem SpawnOne(
            ContentEntry entry, Transform levelAnchor, string levelId,
            int objectIndex, SpawnPoint point, ParkSimReport report)
        {
            GameObject instance;
            try {
                instance = InstantiateSource(entry);
            } catch (System.Exception e) {
                // Core swallows per-item spawn failures so one bad prefab
                // cannot take down the level. Same rule here.
                Debug.LogError("[ParkSim] Failed to spawn " + entry.displayName + ": " + e.Message);
                return null;
            }
            if (instance == null) return null;

            instance.name = entry.displayName;

            // Immediately after Instantiate and before ANY yield — a
            // NavMeshAgent binds to the nearest navmesh point the moment it
            // enables, which is now, while the object is still at the prefab's
            // pose. See NavAgentPlacementGuard.
            NavAgentPlacementGuard.Suspend(instance);

            instance.transform.SetParent(levelAnchor, true);
            instance.transform.localRotation = Quaternion.identity;

            // A spawn marker represents where the PORTAL goes, not where the
            // attraction's pivot goes — the same thing PortalAnchor.NewLevel
            // means when it offsets a freshly placed level by its
            // defaultAnchorPosition. Skipping this would put the marker at the
            // attraction's arbitrary authoring origin instead of at the point
            // the creator nominated as its entrance, and every attraction would
            // sit a little way off from where the operator placed it.
            instance.transform.localPosition = EntranceOffset(instance);

            var scope = instance.AddComponent<NetScope>();
            scope.scopeKey = levelId + "|" + objectIndex + "|" + entry.displayName;

            // Floor data must land BEFORE LevelTemplate.Start builds the grid,
            // because BuildNavSurfaceAndAnchors is what hands it to the
            // calibrator. Setting it after would be silently ignored.
            string cacheKey = CacheKey(entry, point);
            bool replayed = false;
            if (ParkSimSettings.ReplayCachedFloorData && _floorCache.TryGetValue(cacheKey, out JSONObject cached)) {
                var lt = instance.GetComponent<LevelTemplate>();
                if (lt != null) { lt.floorData = cached; replayed = true; }

                var pt = instance.GetComponent<PropTemplate>();
                if (pt != null) { pt.pointData = cached; replayed = true; }
            }

            NavAgentPlacementGuard.ReleaseIfPlayMode(instance);
            Register(instance, true);

            var item = new PlacedItem {
                name = entry.displayName,
                kind = entry.kind,
                fromUnappliedOverrides = entry.hasUnappliedOverrides,
                fromSample = entry.fromSample,
                assetPath = entry.assetPath,
                marker = point.markerName,
                floorReplayed = replayed,
                instance = instance.transform,
                simulatorOwned = true,
                external = entry.external,
                externalId = entry.externalId,
                externalOrigin = entry.externalOrigin,
            };
            report.items.Add(item);

            if (entry.hasUnappliedOverrides) {
                ParkSimWarningFlag.Attach(levelAnchor, instance, entry.displayName);
            }

            return item;
        }

        /// Fresh placement calibration — the operator-commits-a-placement path
        /// (LevelAnchor.AutoCalibrateNewObject). Only for content the simulator
        /// placed that did not already receive cached floor data, so a
        /// regenerate exercises the load path where it can and the placement
        /// path where it cannot.
        private static IEnumerator CalibrateFresh(ParkSimReport report, bool hasGroundMesh)
        {
            // One frame so every LevelTemplate.Start has built its grid and
            // attached its CalibrateLevel.
            yield return null;

            foreach (var item in report.items) {
                if (!item.simulatorOwned) continue;
                if (item.instance == null || item.floorReplayed) continue;

                var calibrateLevel = item.instance.GetComponentInChildren<CalibrateLevel>(true);
                if (calibrateLevel != null) {
                    bool applied = calibrateLevel.ConformOnce();
                    if (!applied && hasGroundMesh) {
                        // Without a ground mesh this is expected and already
                        // reported once for the whole park; repeating it per
                        // attraction would bury the notes that matter.
                        report.notes.Add(
                            item.name + " did not bake a floor at " + item.marker +
                            " — coverage gate not met, so it is sitting flat.");
                    }
                    continue;
                }

                var calibrateProp = item.instance.GetComponentInChildren<CalibrateProp>(true);
                if (calibrateProp != null) calibrateProp.CalibrateSinglePoint();
            }
        }

        /// Core's ReleaseParkPhysics: wait for every template floor, then for
        /// the GapFiller to have filled between them, and only then unlock. A
        /// park that releases early drops rigidbodies through a floor that does
        /// not exist yet.
        private static IEnumerator ReleaseParkPhysics(ParkSimReport report, Transform ownedRoot)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            // Only the simulator's own templates: a source's park finished its
            // own floors before it handed control back, and waiting on them
            // again would stall this generation on work already done.
            var templates = ownedRoot != null
                ? ownedRoot.GetComponentsInChildren<LevelTemplate>(true)
                : new LevelTemplate[0];
            while (Time.realtimeSinceStartup < deadline) {
                bool allReady = true;
                foreach (var t in templates) {
                    if (!t.generateFloor) continue;
                    if (t.runtimePlane == null) { allReady = false; break; }
                }
                if (allReady) break;
                yield return null;
            }

            var gapFiller = GapFiller.Instance;
            if (gapFiller != null) {
                int target = gapFiller.RequestParkLoadGeneration();
                float gapDeadline = Time.realtimeSinceStartup + 20f;
                while (gapFiller.FloorReadyGenerations < target &&
                       Time.realtimeSinceStartup < gapDeadline) {
                    yield return null;
                }
                if (gapFiller.FloorReadyGenerations < target) {
                    report.notes.Add("GapFiller did not finish within 20s — released anyway.");
                }
            }

            ParkBuilder.LevelObjectManager.UnlockParkContent();
            ParkBuilder.LevelObjectManager.objectsEnabled = true;
            if (ParkBuilder.LevelObjectManager.Instance != null)
                ParkBuilder.LevelObjectManager.Instance.ReleaseAllLevelObjects();
        }

        private static void CacheFloorData(ParkSimReport report)
        {
            foreach (var item in report.items) {
                if (!item.simulatorOwned) continue;
                if (item.instance == null || item.floorReplayed) continue;

                var calibrateLevel = item.instance.GetComponentInChildren<CalibrateLevel>(true);
                if (calibrateLevel != null && calibrateLevel.hasFloorData) {
                    _floorCache[item.name + "@" + item.marker] = calibrateLevel.CompileCalibrationData();
                    continue;
                }

                var calibrateProp = item.instance.GetComponentInChildren<CalibrateProp>(true);
                if (calibrateProp != null && calibrateProp.calibrated) {
                    _floorCache[item.name + "@" + item.marker] = calibrateProp.CompileCalibrationData();
                }
            }
        }

        private static string CacheKey(ContentEntry entry, SpawnPoint point)
        {
            return entry.displayName + "@" + point.markerName;
        }

        /// Mirrors PortalAnchor.NewLevel: a level offsets by its
        /// defaultAnchorPosition, a standalone prop by its
        /// footprintOffsetMeters, and anything carrying neither sits on its own
        /// pivot. Negated because the offset describes where the anchor sits
        /// relative to the content, and we are placing the content relative to
        /// the anchor.
        private static Vector3 EntranceOffset(GameObject instance)
        {
            var level = instance.GetComponent<LevelTemplate>();
            if (level != null) {
                return new Vector3(-level.defaultAnchorPosition.x, 0f, -level.defaultAnchorPosition.y);
            }

            var prop = instance.GetComponent<PropTemplate>();
            if (prop != null) {
                return new Vector3(-prop.footprintOffsetMeters.x, 0f, -prop.footprintOffsetMeters.y);
            }

            return Vector3.zero;
        }

        // ── Plumbing ─────────────────────────────────────────────────────

        private static void EnsureOptimizedAF(List<string> notes)
        {
            var existing = Object.FindFirstObjectByType<ParkBuilder.LevelObjectManager>(FindObjectsInactive.Include);
            if (existing != null) return;

            // The shipping prefab carries BOTH OptimizedAF (the culler) and
            // LevelObjectManager (the registry) configured exactly as core runs
            // them, including the release optimization settings. Instantiating
            // it rather than adding the components by hand is what makes the
            // distance bands and frame interval identical to production.
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/DreamPark/Scripts/Core/OptimizedAF/OptimizedAF.prefab");

            if (prefab == null) {
                notes.Add("OptimizedAF.prefab not found — culling and recovery will not be simulated.");
                return;
            }

            var instance = Object.Instantiate(prefab);
            instance.name = "[ParkSim] OptimizedAF";
            instance.transform.SetParent(_root.transform, true);

            var optimizer = instance.GetComponent<OptimizedAF>();
            if (optimizer != null && optimizer.settings == null) {
                notes.Add("OptimizedAF has no OptimizationSettings assigned — it will throw every FixedUpdate.");
            }
        }

        private static GameObject InstantiateSource(ContentEntry entry)
        {
            if (entry.sceneTemplate != null) {
                // Duplicating the DISABLED scene object, so the copy arrives
                // inactive and its Awake/Start do not run until it is enabled
                // below — after the transform and floor data are in place.
                var copy = Object.Instantiate(entry.sceneTemplate);
                copy.SetActive(true);
                return copy;
            }
            return entry.prefabAsset != null ? Object.Instantiate(entry.prefabAsset) : null;
        }

        private static void Register(GameObject instance, bool startDisabled)
        {
            var manager = ParkBuilder.LevelObjectManager.Instance;
            if (manager != null) manager.RegisterLevelObject(instance, startDisabled);
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        private static void DisableSceneTemplates(ScanResult scan)
        {
            foreach (var entry in scan.placeables) {
                if (entry.sceneTemplate == null) continue;
                if (entry.sceneTemplate.activeSelf) {
                    entry.sceneTemplate.SetActive(false);
                    _disabledSceneTemplates.Add(entry.sceneTemplate);
                }
            }
            if (scan.player != null && scan.player.sceneTemplate != null &&
                scan.player.sceneTemplate.activeSelf) {
                scan.player.sceneTemplate.SetActive(false);
                _disabledSceneTemplates.Add(scan.player.sceneTemplate);
            }
        }

        private static void FlushSuspensions()
        {
            foreach (var go in _pendingSuspension) {
                if (go == null) continue;
                if (go.activeSelf) {
                    go.SetActive(false);
                    _disabledSceneTemplates.Add(go);
                }
            }
            _pendingSuspension.Clear();
        }

        private static void Teardown()
        {
            // Stop any patrol before the park it is walking around disappears.
            ParkSimCamera.Reset();

            // Before the root goes: a source may own objects outside it, or
            // hold state that a bare DestroyImmediate would strand.
            if (_source != null) {
                try { _source.Teardown(); }
                catch (System.Exception e) { Debug.LogException(e); }
            }

            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
            _environment = null;

            // Scene templates stay disabled for the session: they are the spawn
            // source for every regenerate, and re-enabling them would put a
            // second copy of the attraction back in the scene alongside the one
            // in the park.
            _pendingSuspension.Clear();
        }

        private static int CountDirty(ParkSimReport report)
        {
            int n = 0;
            foreach (var i in report.items) if (i.fromUnappliedOverrides) n++;
            return n;
        }

        private static int CountOwned(ParkSimReport report)
        {
            int n = 0;
            foreach (var i in report.items) if (i.simulatorOwned) n++;
            return n;
        }

        private static string Sanitize(string s)
        {
            return string.IsNullOrEmpty(s) ? "object" : s.Replace(' ', '-');
        }
    }
}
