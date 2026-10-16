using System;
using System.Collections.Generic;
using DreamPark.ParkBuilder;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Playables;
using XLua;

namespace DreamPark {
    /// <summary>
    /// ── THE GAME PAUSE (Oct 2026): an operator / parent holds the game ──
    ///
    /// Aidan: "I would definitely like the headset pause … for operators who
    /// need to hold up a game … basically a parental control and operator
    /// control feature." Core decides WHEN (a Location's persisted hold, the
    /// phone's LAN broadcast — core LocationPlayback.cs); this class is HOW,
    /// and it is the same on a headset, a phone and in the SDK Editor.
    ///
    /// WHAT FREEZES. Exactly the park content LevelObjectManager already
    /// knows how to park — every registered LevelObject that is not priority
    /// (the player rig, trigger and gizmo layers stay live, so tracking, hands,
    /// the DreamBand and zone triggers keep working; the player can look
    /// around a frozen world). It does NOT touch Time.timeScale (XR, UI and the
    /// DreamBand all run on it) and it does NOT flip `objectsEnabled` (that is
    /// the park-load / build switch — LuaBehaviour boot and onready() read it).
    ///
    /// HOW, per object, chosen so RESUME RESTORES EXACTLY — not the culling
    /// path, which stops and clears things because it only ever has to be
    /// right about "far away":
    ///   Rigidbody      kinematic + no collisions, velocity saved → put back
    ///                  (the optimizer's own RigidbodySettings snapshot)
    ///   Collider       disabled (trigger messages reach DISABLED scripts, so
    ///                  a frozen pickup must not be collectable)
    ///   AudioSource    Pause() → UnPause() (disabling one would restart it)
    ///   Animator       speed 0 → saved speed (disabling resets the state)
    ///   ParticleSystem Pause() → Play() (resumes where it froze)
    ///   NavMeshAgent   isStopped → saved isStopped + velocity
    ///   PlayableDirector Pause() → Resume()
    ///   MonoBehaviour  disabled → re-enabled (Lua update() and every script
    ///                  stop) — except UI (UIBehaviour), which is how it LOOKS
    ///   Light/Camera/Canvas/Renderer  untouched: the frozen world stays lit
    ///                  and visible
    /// Template-root Rigidbodies (a placed prop's own body) are parked too.
    ///
    /// THE PLAYERS (Oct 14 2026, playtest-oct13 — Aidan: "pausing didn't actually
    /// pause the gameplay. LaserTag guns still shot bullets"). Every Player.prefab
    /// registers as a PRIORITY LevelObject (its name / its PlayerRig), which the
    /// freeze above never touches — and a title's rig is far more than hands:
    /// LaserTag's blaster (auto-fire), bolts, session and weapon managers all
    /// live on Player.prefab, and no title honoured onpause(). So the platform
    /// freezes the rigs too (FreezePlayers), with no title opt-in:
    ///   local rigs   every PlayerRig not inside a RemotePlayer (shown or not):
    ///                scripts (Lua included) off, colliders off, audio paused,
    ///                animators / particles held, renderers HIDDEN
    ///                (HideLocalRigs: the player is out of the game, and a frozen
    ///                gun must not hang in the hand). The RemoteRig template is
    ///                skipped (peers clone it).
    ///   remote rigs  each RemotePlayer's RemoteRig clone: the same freeze, still
    ///                drawn — RemoteRig keeps it on the peer, nothing on it acts.
    ///   kept live    PlayerRig, Head/Hand/Body/FeetTracker, NetId, NetScope,
    ///                RemoteRig, UI — tracking and identity, never gameplay.
    /// A rig that appears (or anything new on a frozen rig) while paused is
    /// frozen within RigRescanSeconds; the resume restores exactly what changed.
    ///
    /// WHILE PAUSED the optimizer keeps culling (LevelObject.Enable(false)
    /// thaws first, so its re-read sees the GAME's state, never the freeze's),
    /// but nothing parked may come back to life: Enable(true) is deferred and
    /// replayed on resume, through the same global clamps (load lock, build
    /// mode, forceDisabled). Content registered mid-pause (a title loading) is
    /// frozen on arrival.
    ///
    /// LUA: onpause() fires BEFORE the freeze (scripts are still running, so a
    /// Player.prefab manager can stop its own clock / music), onresume() AFTER
    /// the thaw (park AND rigs). Sticky like every DreamPark event: a script
    /// that boots while paused gets onpause() when it is wired. dp.paused(),
    /// dp.pause_info() and dp.game_time() (Time.time with the paused stretches
    /// removed).
    ///
    /// KNOWN LIMITS: C# coroutines and InvokeRepeating keep running (Unity
    /// does not stop them for a disabled behaviour — the build-mode park has
    /// the same hole); Time.time keeps moving (use dp.game_time()); a VFX
    /// Graph / VideoPlayer is not paused.
    /// </summary>
    public static class GamePause {
        public struct Info {
            /// Display name of who paused ("Aidan"), or empty.
            public string byName;
            /// owner | coowner | parent | editor — or empty.
            public string byRole;
            /// The operator's reason ("Snack break"), or empty.
            public string reason;
            /// What delivered it: lan | server | editor | phone.
            public string source;
            public long pausedAtMs;
        }

        /// <summary>Kill switch (false = Pause is refused; Resume still works).</summary>
        public static bool Enabled = true;

        public static bool IsPaused { get; private set; }
        public static Info Current { get; private set; }

        /// <summary>true BEFORE the freeze (scripts still running); false AFTER the thaw.</summary>
        public static event Action<bool> OnPausedChanged;

        static double s_pausedAt;
        static double s_pausedTotal;
        /// <summary>Seconds of Time.time spent paused this run (dp.game_time = Time.time − this).</summary>
        public static double PausedSecondsTotal =>
            s_pausedTotal + (IsPaused ? Math.Max(0.0, Time.timeAsDouble - s_pausedAt) : 0.0);
        public static float GameTime => (float)(Time.timeAsDouble - PausedSecondsTotal);
        /// <summary>Seconds this pause has lasted (0 while playing).</summary>
        public static float CurrentPauseSeconds => IsPaused ? (float)Math.Max(0.0, Time.timeAsDouble - s_pausedAt) : 0f;

        static readonly Dictionary<LevelObject, Frozen> s_frozen = new Dictionary<LevelObject, Frozen>();
        static readonly HashSet<LevelObject> s_deferred = new HashSet<LevelObject>();

        /// <summary>Diagnostics / tests: objects frozen right now, and parked objects waiting for the resume.</summary>
        public static int FrozenCount => s_frozen.Count;
        public static int DeferredCount => s_deferred.Count;
        public static bool IsFrozen(LevelObject lo) => lo != null && s_frozen.ContainsKey(lo);

        // ── The two edges ──────────────────────────────────────────────

        /// <summary>Freeze the game. Already paused → only the info updates (returns false).</summary>
        public static bool Pause(Info info) {
            if (!Enabled) return false;
            if (IsPaused) { Current = info; return false; }
            if (info.pausedAtMs <= 0) info.pausedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Current = info;
            // onpause() first, while every script can still act.
            IsPaused = true;
            s_pausedAt = Time.timeAsDouble;
            Raise(true);
            int frozen = 0;
            var lom = LevelObjectManager.Instance;
            if (lom != null) {
                var snapshot = lom.levelObjects.ToArray();
                for (int i = 0; i < snapshot.Length; i++) if (TryFreeze(snapshot[i])) frozen++;
                lom.PauseTemplateRootBodies();
            }
            int rigs = FreezePlayersNow();
            EnsurePlayersDriver();
            Debug.Log($"[GamePause] PAUSED by {Label(info)} — froze {frozen} object(s)" +
                      (FreezePlayers ? $" and {rigs} player rig(s) ({(HideLocalRigs ? "local rigs hidden" : "local rigs shown")})" : "; player rigs stay live (FreezePlayers off)") +
                      "; tracking / DreamBand stay live");
            return true;
        }

        /// <summary>Thaw everything exactly as it was. Not paused → no-op (returns false).</summary>
        public static bool Resume(string source = null) {
            if (!IsPaused) return false;
            IsPaused = false;
            s_pausedTotal += Math.Max(0.0, Time.timeAsDouble - s_pausedAt);
            int thawed = 0;
            var frozen = new List<LevelObject>(s_frozen.Keys);
            foreach (var lo in frozen) {
                if (!Thaw(lo)) continue;
                thawed++;
                // Whatever asked for this object to be parked while it was frozen
                // (load lock, build mode, forceDisabled) is honoured now, through
                // the normal path: Enable(true) applies every global clamp.
                lo.Enable(true);
            }
            s_frozen.Clear();
            var deferred = new List<LevelObject>(s_deferred);
            s_deferred.Clear();
            foreach (var lo in deferred) if (lo != null && lo.gameObject != null) lo.Enable(true);
            LevelObjectManager.Instance?.ResumeTemplateRootBodies();
            int rigs = ThawPlayers();
            Debug.Log($"[GamePause] RESUMED ({source ?? Current.source}) — thawed {thawed} object(s) and {rigs} player rig(s), replayed {deferred.Count} deferred enable(s)");
            Raise(false);
            return true;
        }

        static void Raise(bool paused) {
            var handlers = OnPausedChanged;
            if (handlers == null) return;
            foreach (Action<bool> h in handlers.GetInvocationList()) {
                try { h(paused); }
                catch (Exception e) { Debug.LogError("[GamePause] OnPausedChanged subscriber threw: " + e); }
            }
        }

        static string Label(Info i) {
            string who = string.IsNullOrEmpty(i.byName) ? (string.IsNullOrEmpty(i.byRole) ? "the game master" : i.byRole) : i.byName;
            return who + (string.IsNullOrEmpty(i.source) ? "" : " via " + i.source)
                       + (string.IsNullOrEmpty(i.reason) ? "" : " (" + i.reason + ")");
        }

        // ── LevelObjectManager hooks ───────────────────────────────────

        /// <summary>LevelObject.Enable(true) on a PARKED object while paused: wait for the resume.</summary>
        internal static void DeferEnable(LevelObject lo) { if (lo != null) s_deferred.Add(lo); }
        internal static void ClearDeferred(LevelObject lo) { if (lo != null) s_deferred.Remove(lo); }

        /// <summary>A LevelObject registered while paused, live (not startDisabled): freeze it now.</summary>
        internal static void NoteRegistered(LevelObject lo) { if (IsPaused) TryFreeze(lo); }

        static bool TryFreeze(LevelObject lo) {
            if (lo == null || lo.isPriority || !lo.enabled || lo.gameObject == null) return false;
            if (s_frozen.ContainsKey(lo)) return false;
            var f = new Frozen();
            LuaBehaviour.OptimizerToggleDepth++;
            try { f.Capture(lo); }
            catch (Exception e) { Debug.LogWarning($"[GamePause] freeze {lo.name} failed: {e.Message}"); }
            finally { LuaBehaviour.OptimizerToggleDepth--; }
            s_frozen[lo] = f;
            return true;
        }

        /// <summary>Put one frozen object back exactly (also called before the optimizer parks it mid-pause).</summary>
        internal static bool Thaw(LevelObject lo) {
            if (lo == null || !s_frozen.TryGetValue(lo, out var f)) return false;
            s_frozen.Remove(lo);
            LuaBehaviour.OptimizerToggleDepth++;
            try { f.Restore(); }
            catch (Exception e) { Debug.LogWarning($"[GamePause] thaw {lo.name} failed: {e.Message}"); }
            finally { LuaBehaviour.OptimizerToggleDepth--; }
            return true;
        }

        // ── dp.enabled / dp.set_enabled while frozen (Oct 2026) ─────────
        //
        //  The freeze switches colliders and scripts off and remembers only the
        //  ones IT switched off, so a frozen component's live `.enabled` is the
        //  freeze's, not the game's. LevelObjectManager.TryGetIntendedEnabled /
        //  SetIntendedEnabled ask here before falling back to the live flag. Both
        //  answer only for the kinds the freeze manages (a Collider, or a
        //  non-UI MonoBehaviour / Animation that is not protected); everything
        //  else — Light, AudioSource, Renderer, Animator, UI — is untouched by the
        //  freeze, so the live flag is already the truth.

        /// <summary>True (with the value the resume will leave) when <paramref name="lo"/> is frozen and the freeze manages <paramref name="c"/>.</summary>
        internal static bool TryGetFrozenIntended(LevelObject lo, Component c, out bool on) {
            on = false;
            if (lo == null || c == null || !s_frozen.TryGetValue(lo, out var f) || !Frozen.Manages(lo, c)) return false;
            on = f.Holds(c);
            return true;
        }

        /// <summary>
        /// Frozen + managed: record the intent for the resume and keep the component off
        /// meanwhile (true). Otherwise false, and the caller writes the live flag.
        /// </summary>
        internal static bool TrySetFrozenIntended(LevelObject lo, Component c, bool on) {
            if (lo == null || c == null || !s_frozen.TryGetValue(lo, out var f) || !Frozen.Manages(lo, c)) return false;
            // Same window as the freeze itself: keeping a script off here is engine
            // state, not a gameplay ondisable().
            LuaBehaviour.OptimizerToggleDepth++;
            try { f.SetHeld(c, on); }
            finally { LuaBehaviour.OptimizerToggleDepth--; }
            return true;
        }

        /// <summary>One object's freeze: only what WE changed, so the restore writes nothing else.</summary>
        sealed class Frozen {
            readonly List<Collider> colliders = new List<Collider>();
            readonly List<LevelObject.RigidbodySettings> bodies = new List<LevelObject.RigidbodySettings>();
            readonly List<Behaviour> behaviours = new List<Behaviour>();
            readonly List<AudioSource> audio = new List<AudioSource>();
            readonly List<KeyValuePair<Animator, float>> animators = new List<KeyValuePair<Animator, float>>();
            readonly List<ParticleSystem> particles = new List<ParticleSystem>();
            readonly List<PlayableDirector> directors = new List<PlayableDirector>();
            readonly List<AgentState> agents = new List<AgentState>();

            struct AgentState { public NavMeshAgent agent; public bool stopped; public Vector3 velocity; }

            public void Capture(LevelObject lo) {
                if (lo.colliders != null) foreach (var cs in lo.colliders) {
                    var c = cs?.collider;
                    if (c == null || !c.enabled) continue;
                    c.enabled = false; colliders.Add(c);
                }
                if (lo.rigidbodies != null) foreach (var rs in lo.rigidbodies) {
                    if (rs?.rigidbody == null) continue;
                    if (rs.Toggle(false)) bodies.Add(rs);
                }
                if (lo.components != null) foreach (var cs in lo.components) {
                    if (cs == null || cs.IsProtected) continue;
                    var comp = cs.component;
                    if (comp == null) continue;
                    switch (comp) {
                        case AudioSource a:
                            if (a.isPlaying) { a.Pause(); audio.Add(a); }
                            break;
                        case NavMeshAgent n:
                            if (n.isActiveAndEnabled && n.isOnNavMesh && !n.isStopped) {
                                agents.Add(new AgentState { agent = n, stopped = n.isStopped, velocity = n.velocity });
                                n.isStopped = true; n.velocity = Vector3.zero;
                            }
                            break;
                        case PlayableDirector d:
                            if (d.state == PlayState.Playing) { d.Pause(); directors.Add(d); }
                            break;
                        case Animation an:
                            if (an.enabled) { an.enabled = false; behaviours.Add(an); }
                            break;
                        case UnityEngine.EventSystems.UIBehaviour _:
                            break;   // UI is how the frozen world LOOKS — leave it on
                        case MonoBehaviour mb:
                            if (mb.enabled) { mb.enabled = false; behaviours.Add(mb); }
                            break;
                    }
                }
                if (lo.animators != null) foreach (var cs in lo.animators) {
                    if (cs?.component is Animator an && an != null && an.enabled && an.speed != 0f) {
                        animators.Add(new KeyValuePair<Animator, float>(an, an.speed));
                        an.speed = 0f;
                    }
                }
                if (lo.particleSystems != null) foreach (var ps in lo.particleSystems) {
                    var p = ps?.particleSystem;
                    if (p == null || !p.isPlaying) continue;
                    p.Pause(false); particles.Add(p);
                }
            }

            /// <summary>The kinds Capture switches off (and Restore switches back on).</summary>
            internal static bool Manages(LevelObject lo, Component c) {
                if (c is Collider) {
                    if (lo.colliders != null) foreach (var cs in lo.colliders) if (cs != null && ReferenceEquals(cs.collider, c)) return true;
                    return false;
                }
                if (!(c is Animation) && (!(c is MonoBehaviour) || c is UnityEngine.EventSystems.UIBehaviour)) return false;
                if (lo.components != null) foreach (var cs in lo.components)
                    if (cs != null && ReferenceEquals(cs.component, c)) return !cs.IsProtected;
                return false;
            }

            /// <summary>Will Restore switch <paramref name="c"/> back on?</summary>
            internal bool Holds(Component c) => c is Collider col ? colliders.Contains(col)
                                              : c is Behaviour b && behaviours.Contains(b);

            /// <summary>Make Restore switch <paramref name="c"/> on (true) or leave it off (false).</summary>
            internal void SetHeld(Component c, bool on) {
                if (c is Collider col) {
                    if (on) { if (!colliders.Contains(col)) colliders.Add(col); if (col.enabled) col.enabled = false; }
                    else colliders.Remove(col);
                } else if (c is Behaviour b) {
                    if (on) { if (!behaviours.Contains(b)) behaviours.Add(b); if (b.enabled) b.enabled = false; }
                    else behaviours.Remove(b);
                }
            }

            public void Restore() {
                // Reverse order of Capture: bodies come back before the scripts that push them.
                foreach (var c in colliders) if (c != null) c.enabled = true;
                foreach (var rs in bodies) rs.Toggle(true);
                foreach (var a in agents) {
                    if (a.agent == null || !a.agent.isActiveAndEnabled || !a.agent.isOnNavMesh) continue;
                    a.agent.isStopped = a.stopped;
                    a.agent.velocity = a.velocity;
                }
                foreach (var d in directors) if (d != null) d.Resume();
                foreach (var kv in animators) if (kv.Key != null) kv.Key.speed = kv.Value;
                foreach (var p in particles) if (p != null && p.isPaused) p.Play(false);
                foreach (var a in audio) if (a != null) a.UnPause();
                foreach (var b in behaviours) if (b != null) b.enabled = true;
            }
        }

        // ── The players (Oct 14 2026) ──────────────────────────────────
        //
        //  See the class summary. Rigs are not LevelObjects the freeze can use
        //  (they are priority), so each rig root gets its own record of exactly
        //  what the pause switched off; FreezePlayersNow is idempotent (it only
        //  adds what is new, and re-asserts what something switched back on).

        /// <summary>Kill switch: false = the pre-Oct 14 rule (player rigs stay live while paused).</summary>
        public static bool FreezePlayers = true;
        /// <summary>Local rigs are hidden while paused (false = frozen in place, still drawn).</summary>
        public static bool HideLocalRigs = true;
        /// <summary>How often a running pause looks for rigs that appeared / changed.</summary>
        public const float RigRescanSeconds = 0.25f;

        static readonly Dictionary<GameObject, FrozenRig> s_rigs = new Dictionary<GameObject, FrozenRig>();
        static readonly List<GameObject> s_rigScratch = new List<GameObject>();
        static readonly List<Component> s_componentScratch = new List<Component>();
        static GameObject s_driver;

        /// <summary>Diagnostics / tests: rigs frozen right now.</summary>
        public static int FrozenRigCount => s_rigs.Count;
        public static bool IsRigFrozen(GameObject rigRoot) => rigRoot != null && s_rigs.ContainsKey(rigRoot);

        /// <summary>
        /// Pure: the component types a rig freeze leaves running — tracking and identity,
        /// never gameplay (UI is how the rig LOOKS; the hide handles that).
        /// </summary>
        public static bool RigKeepsRunning(Type t) =>
            t != null && (typeof(PlayerRig).IsAssignableFrom(t) || typeof(HeadTracker).IsAssignableFrom(t) ||
                          typeof(HandTracker).IsAssignableFrom(t) || typeof(BodyTracker).IsAssignableFrom(t) ||
                          typeof(FeetTracker).IsAssignableFrom(t) || typeof(NetId).IsAssignableFrom(t) ||
                          typeof(NetScope).IsAssignableFrom(t) || typeof(RemoteRig).IsAssignableFrom(t) ||
                          typeof(RemotePlayer).IsAssignableFrom(t) ||
                          typeof(UnityEngine.EventSystems.UIBehaviour).IsAssignableFrom(t));

        /// <summary>
        /// While paused: freeze every local and remote rig not frozen yet, and anything
        /// new on one that is. Called by Pause and every RigRescanSeconds. Returns the
        /// number of rigs frozen now.
        /// </summary>
        public static int FreezePlayersNow() {
            if (!IsPaused || !FreezePlayers) return 0;
            PruneRigs();
            // Local rigs: registered, and any in the scene (a duplicate, a rig whose
            // Awake has not run yet), shown or hidden — a hidden one GameArea shows
            // mid-pause must not come up live.
            s_rigScratch.Clear();
            if (PlayerRig.instances != null)
                foreach (var kv in PlayerRig.instances) AddLocalRig(kv.Value);
            foreach (var r in UnityEngine.Object.FindObjectsByType<PlayerRig>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                AddLocalRig(r);
            for (int i = 0; i < s_rigScratch.Count; i++) {
                var root = s_rigScratch[i];
                var rig = root.GetComponent<PlayerRig>();
                Transform template = rig != null && rig.remoteRigTemplate != null ? rig.remoteRigTemplate : RemoteRig.FindTemplate(root.transform);
                FreezeRig(root, HideLocalRigs, template);
            }
            // Remote rigs: what other players look like here.
            var presence = PlayerPresence.Instance;
            if (presence != null)
                foreach (var p in presence.Peers.Values)
                    if (p != null && p.rig != null) FreezeRig(p.rig, false, null);
            return s_rigs.Count;
        }

        static void AddLocalRig(PlayerRig r) {
            if (r == null || r.gameObject == null) return;
            if (r.GetComponentInParent<RemotePlayer>(true) != null) return;
            if (!s_rigScratch.Contains(r.gameObject)) s_rigScratch.Add(r.gameObject);
        }

        static void FreezeRig(GameObject root, bool hide, Transform skip) {
            if (!s_rigs.TryGetValue(root, out var f)) { f = new FrozenRig(); s_rigs[root] = f; }
            LuaBehaviour.OptimizerToggleDepth++;
            try { f.Capture(root, hide, skip); }
            catch (Exception e) { Debug.LogWarning($"[GamePause] freeze rig {root.name} failed: {e.Message}"); }
            finally { LuaBehaviour.OptimizerToggleDepth--; }
        }

        static void PruneRigs() {
            List<GameObject> dead = null;
            foreach (var kv in s_rigs) if (kv.Key == null) (dead ??= new List<GameObject>()).Add(kv.Key);
            if (dead != null) foreach (var k in dead) s_rigs.Remove(k);
        }

        static int ThawPlayers() {
            int n = 0;
            foreach (var kv in s_rigs) {
                if (kv.Key == null) continue;
                LuaBehaviour.OptimizerToggleDepth++;
                try { kv.Value.Restore(); n++; }
                catch (Exception e) { Debug.LogWarning($"[GamePause] thaw rig {kv.Key.name} failed: {e.Message}"); }
                finally { LuaBehaviour.OptimizerToggleDepth--; }
            }
            s_rigs.Clear();
            return n;
        }

        static void EnsurePlayersDriver() {
            if (!Application.isPlaying || s_driver != null) return;
            s_driver = new GameObject("~GamePausePlayers") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(s_driver);
            s_driver.AddComponent<GamePausePlayersDriver>();
        }

        /// <summary>One rig's freeze: only what WE changed, so the restore writes nothing else.</summary>
        sealed class FrozenRig {
            readonly HashSet<Behaviour> behaviours = new HashSet<Behaviour>();
            readonly HashSet<Collider> colliders = new HashSet<Collider>();
            readonly HashSet<Renderer> renderers = new HashSet<Renderer>();
            readonly HashSet<AudioSource> audio = new HashSet<AudioSource>();
            readonly Dictionary<Animator, float> animators = new Dictionary<Animator, float>();
            readonly HashSet<ParticleSystem> particles = new HashSet<ParticleSystem>();

            public void Capture(GameObject root, bool hide, Transform skip) {
                s_componentScratch.Clear();
                root.GetComponentsInChildren(true, s_componentScratch);
                foreach (var c in s_componentScratch) {
                    if (c == null) continue;
                    if (skip != null && c.transform.IsChildOf(skip)) continue;   // the RemoteRig template peers clone
                    switch (c) {
                        case Renderer r:
                            if (hide && r.enabled) { r.enabled = false; renderers.Add(r); }
                            break;
                        case Collider col:
                            if (col.enabled) { col.enabled = false; colliders.Add(col); }
                            break;
                        case AudioSource a:
                            if (a.isPlaying) { a.Pause(); audio.Add(a); }
                            break;
                        case Animator an:
                            if (an.speed != 0f && !animators.ContainsKey(an)) { animators[an] = an.speed; an.speed = 0f; }
                            break;
                        case ParticleSystem ps:
                            if (ps.isPlaying) { ps.Pause(false); particles.Add(ps); }
                            break;
                        case Animation anim:
                            if (anim.enabled) { anim.enabled = false; behaviours.Add(anim); }
                            break;
                        case MonoBehaviour mb:
                            if (mb.enabled && !RigKeepsRunning(mb.GetType())) { mb.enabled = false; behaviours.Add(mb); }
                            break;
                    }
                }
                s_componentScratch.Clear();
            }

            public void Restore() {
                // Bodies' contacts before the scripts that use them (as the park thaw).
                foreach (var c in colliders) if (c != null) c.enabled = true;
                foreach (var kv in animators) if (kv.Key != null) kv.Key.speed = kv.Value;
                foreach (var p in particles) if (p != null && p.isPaused) p.Play(false);
                foreach (var a in audio) if (a != null) a.UnPause();
                foreach (var r in renderers) if (r != null) r.enabled = true;
                foreach (var b in behaviours) if (b != null) b.enabled = true;
            }
        }

        // ── Lua: dp.paused() / dp.pause_info() / dp.game_time() ─────────
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RegisterLua() {
            try {
                var env = LuaBehaviour.GetLuaEnv();
                if (env == null) return;
                env.Global.Set("dp_paused", new Func<bool>(() => IsPaused));
                env.Global.Set("dp_game_time", new Func<float>(() => GameTime));
                env.Global.Set("dp_pause_field", new Func<string, string>(k => {
                    if (!IsPaused) return null;
                    var c = Current;
                    switch (k) {
                        case "by": return c.byName ?? "";
                        case "role": return c.byRole ?? "";
                        case "reason": return c.reason ?? "";
                        case "source": return c.source ?? "";
                        default: return null;
                    }
                }));
                // No double quotes inside this verbatim block.
                env.DoString(@"
dp = dp or {}
-- Operator / parent pause (GamePause.cs). The game is held: your park
-- content AND your Player.prefab are frozen and their scripts are not
-- running (Oct 14 2026). onpause() runs just before the freeze and
-- onresume() just after it: stop / re-base clocks there.
dp.paused = function() return dp_paused() end
dp.pause_info = function()
    if not dp_paused() then return nil end
    return { by = dp_pause_field('by'), role = dp_pause_field('role'),
             reason = dp_pause_field('reason'), source = dp_pause_field('source') }
end
-- Time.time with every paused stretch removed: timers built on it neither
-- jump nor expire across a pause.
dp.game_time = function() return dp_game_time() end
", "dp_pause");
            } catch (Exception e) {
                Debug.LogWarning("[GamePause] Lua registration failed: " + e.Message);
            }
        }

        /// <summary>Tests: forget everything (never call in a shipped flow).</summary>
        public static void ResetForTests() {
            IsPaused = false; Current = default; s_frozen.Clear(); s_deferred.Clear(); s_rigs.Clear();
            s_pausedAt = 0; s_pausedTotal = 0;
        }
    }

    /// <summary>GamePause: while paused, freezes rigs that appear or change (GamePause.RigRescanSeconds).</summary>
    internal sealed class GamePausePlayersDriver : MonoBehaviour {
        float _next;
        void Update() {
            if (!GamePause.IsPaused) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + GamePause.RigRescanSeconds;
            GamePause.FreezePlayersNow();
        }
    }
}
