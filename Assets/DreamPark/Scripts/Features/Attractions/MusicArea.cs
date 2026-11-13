namespace DreamPark
{
    using UnityEngine;
    using System;
    using System.Collections;
    using System.Collections.Generic;
#if UNITY_EDITOR
    using UnityEditor;
#endif
    public class MusicArea : MonoBehaviour
    {
        // Music-area control state lives on a scene-scoped object (NOT DontDestroyOnLoad) instead of
        // static fields, so it is destroyed with the scene and rebuilt with defaults the next session.
        // The active-area count and current-area reference therefore reset automatically on a
        // Main.scene reload, with no manual clearing. The statics below are only POINTERS to that
        // state; the data dies with the scene (Unity reports a destroyed object as == null, which
        // drives the rebuild). Names/visibility are unchanged, so callers and external references
        // (e.g. MusicArea.currentMusicArea) keep working exactly as before.
        private sealed class State
        {
            public int activeMusicAreas;
            public int? priority;
            public MusicArea currentMusicArea;
        }
        private sealed class StateHolder : MonoBehaviour { public readonly State state = new State(); }

        private static StateHolder _holder;
#if UNITY_EDITOR
        // Edit-mode access (e.g. OnDestroy on scene close) must not spawn a runtime GameObject.
        private static readonly State _editorState = new State();
#endif
        private static State S
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    return _editorState;
#endif
                if (_holder == null)
                    _holder = new GameObject("[MusicAreaState]").AddComponent<StateHolder>();
                return _holder.state;
            }
        }

        private static int activeMusicAreas { get => S.activeMusicAreas; set => S.activeMusicAreas = value; }
        public static int? _priority { get => S.priority; set => S.priority = value; } // NOTE: currently unused; kept for API compatibility.
        public static MusicArea currentMusicArea { get => S.currentMusicArea; set => S.currentMusicArea = value; }
        public AudioClip musicTrack;
        public float volume = 0.6f;
        public int priority = 0;
        [ReadOnly] public bool isPlaying = false;
        private bool isActive = false;
        private AudioSource audioSource;
        private Vector3 halfExtents = Vector3.zero;
        private LevelTemplate levelTemplate;
        // public AudioSource AudioSource => audioSource;
        private Coroutine duckCoroutine;
        [HideInInspector] public bool isFocused = false;

        // ── Every live music area (the phone director's candidates, MusicAreaPhone) ──
        static readonly List<MusicArea> s_all = new List<MusicArea>();
        /// Every MusicArea that has woken and not been destroyed (with or without a track).
        public static IReadOnlyList<MusicArea> All => s_all;
        /// It made its emitter (it had a track at Awake).
        public bool HasEmitter => audioSource != null;
        /// Its emitter is sounding: playing, unmuted, above a whisper.
        public bool EmitterSounding => audioSource != null && audioSource.isActiveAndEnabled && audioSource.isPlaying
                                       && !audioSource.mute && audioSource.volume >= 0.02f;
        /// Its footprint's half extents (what the presence test uses), also for an area without a track.
        public Vector3 FootprintHalfExtents()
        {
            var lt = levelTemplate != null ? levelTemplate : GetComponent<LevelTemplate>();
            if (lt != null)
            {
                Vector2 b = lt.RuntimeFootprintMeters;
                return new Vector3(b.x / 2f, 50f, b.y / 2f);
            }
            return transform.localScale * 0.5f;
        }

        public virtual void Awake()
        {
            if (Application.isPlaying && !s_all.Contains(this)) s_all.Add(this);   // phone candidates (MusicAreaPhone)
            if (!musicTrack)
            {
                enabled = false;
                return;
            }
            GameObject musicEmitter = new GameObject("MusicEmitter");
            musicEmitter.transform.parent = transform;
            musicEmitter.transform.localPosition = Vector3.zero;
            musicEmitter.transform.localRotation = Quaternion.identity;
            musicEmitter.transform.localScale = Vector3.one;
            // No RealisticRolloff here (removed Oct 2026). It used to be added BEFORE the
            // AudioSource, so its Awake found none, logged "RealisticRolloff script requires
            // at least one AudioSource component" for every music area (every simulated-peer
            // run) and changed nothing, then destroyed itself in Start. Every title's music
            // has therefore always played on Unity's default rolloff; adding it after the
            // source instead would have changed how all shipped music sounds.
            audioSource = musicEmitter.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.volume = volume;
            audioSource.priority = priority;
            audioSource.clip = musicTrack;
            audioSource.spatialBlend = 1;
            audioSource.volume = 0;

            BodyTracker bodyTracker = musicEmitter.AddComponent<BodyTracker>();
            bodyTracker.yOffset = 2f;

            ComputeBounds();
        }

        /// <summary>Refresh after an attraction selects a packed runtime footprint.</summary>
        public void ComputeBounds()
        {
            levelTemplate = GetComponent<LevelTemplate>();
            if (levelTemplate)
            {
                Vector2 bounds2D = levelTemplate.RuntimeFootprintMeters;
                halfExtents = new Vector3(bounds2D.x / 2f, 50f, bounds2D.y / 2f);
            }
            else
            {
                halfExtents = transform.localScale * 0.5f;
            }
        }
        void Update()
        {
            // Build mode is a STAGING view: the operator is arranging a park, not
            // playing it, and there is no player whose presence this should track.
            // The footprint we test is a 100 m tall Y slab (halfExtents.y = 50) tested
            // against Camera.main, so the build camera — which hovers above the park to
            // see it — is inside essentially every music area at once. Without this gate
            // the park sings at you the entire time you are arranging it, and the
            // highest-priority area wins a contest nobody entered.
            //
            // This is also the one place that can stop a PARKED source from resurrecting
            // itself: the repair below (audioSource.enabled = true) runs before any
            // presence test, so a MusicArea whose AudioSource was disabled by the
            // optimizer would re-enable it and Enter() on the following frame.
            // A PHONE (Oct 23 2026, phone-music): no head walks into an area there — the platform picks the one area the
            // phone plays (MusicAreaPhone: the camera's area in AR, the diorama's focus in the diorama), build-parked or not.
            if (MusicAreaPhone.Active)
            {
                // Never let the music path take the app down (Oct 23 crash hunt): any fault turns the phone path off for
                // the session (MusicAreaPhone.Fault) and the area stands down quietly.
                try { PhoneUpdate(); }
                catch (Exception e) { MusicAreaPhone.Fault("MusicArea.PhoneUpdate " + name, e); StandDown(); }
                return;
            }
            if (_phoneDriven)
            {
                // The host stopped driving the phone: hand back to the ordinary rule from silence.
                _phoneDriven = false;
                StandDown();
            }
            if (ParkBuilder.LevelObjectManager.BuildModeParked)
            {
                if (isPlaying || isActive)
                {
                    StandDown();
                }
                return;
            }
            if (isPlaying && !audioSource.isPlaying)
            {
                currentMusicArea = null;
                audioSource.enabled = true;
                isPlaying = false;
            }
            if (!levelTemplate)
            {
                halfExtents = transform.localScale * 0.5f;
            }
            if (Camera.main)
            {
                // (Oct 20: the Editor-only SceneView.lastActiveSceneView.camera read that stood here was unused and threw a
                // NullReferenceException every frame in an Editor with no Scene view open — batch harnesses — so the
                // presence test below never ran and no music area ever entered there.)
                if (IsPointWithinBounds(Camera.main.transform.position, transform, halfExtents))
                {
                    if (!isActive)
                    {
                        activeMusicAreas++;
                        isActive = true;
                    }
                    if (!isPlaying)
                    {
                        Enter();
                    }
                }
                else
                {
                    if (isActive)
                    {
                        activeMusicAreas--;
                        isActive = false;
                    }
                    if (isPlaying)
                    {
                        Exit();
                    }
                }
            }
        }
        // ── The phone path (MusicAreaPhone) ─────────────────────────────
        /// Fade in / out time on the phone (the app's title theme crossfades against it over about the same).
        public const float PhoneFadeSeconds = 1.25f;
        bool _phoneDriven;
        float _phoneLevel, _phoneWritten = -1f;

        void PhoneUpdate()
        {
            _phoneDriven = true;
            MusicAreaPhone.Step();
            if (audioSource == null || audioSource.clip == null) return;
            bool want = MusicAreaPhone.Pick == this;
            if (want != isActive)
            {
                activeMusicAreas += want ? 1 : -1;
                isActive = want;
            }
            if (want)
            {
                MusicArea cur = currentMusicArea;
                if (cur != this)
                {
                    // The same track in the next area carries on where it is (the headset's adopt rule).
                    if (cur != null && cur.audioSource != null && cur.audioSource.clip == audioSource.clip && cur.audioSource.isPlaying)
                    {
                        AdoptPlaybackFrom(cur);
                        _phoneLevel = cur._phoneLevel;
                        cur._phoneLevel = 0f;
                        cur.isPlaying = false;
                    }
                    currentMusicArea = this;   // the old area fades itself out on its own Update
                }
                if (!audioSource.enabled) audioSource.enabled = true;   // a parked emitter (build / scan parking)
                if (!audioSource.isPlaying)
                {
                    if (_phoneLevel <= 0.001f) { audioSource.volume = 0f; _phoneWritten = 0f; }
                    if (audioSource.time > 0f) audioSource.UnPause();
                    if (!audioSource.isPlaying) audioSource.Play();
                }
                isPlaying = true;
            }
            if (!isPlaying && _phoneLevel <= 0f) return;
            float target = want ? volume * MusicAreaPhone.Gain : 0f;
            _phoneLevel = Mathf.MoveTowards(_phoneLevel, target, Time.unscaledDeltaTime * Mathf.Max(volume, 0.05f) / PhoneFadeSeconds);
            // Write only when the level moved: the voice ducker multiplies on top of what we wrote and re-reads it then.
            if (!Mathf.Approximately(_phoneLevel, _phoneWritten))
            {
                audioSource.volume = _phoneLevel;
                _phoneWritten = _phoneLevel;
            }
            if (!want && _phoneLevel <= 0.0001f)
            {
                if (audioSource.isPlaying) audioSource.Pause();
                if (currentMusicArea == this) currentMusicArea = null;
                isPlaying = false;
            }
        }

        public virtual void Enter()
        {
            if (currentMusicArea)
            {
                if (currentMusicArea != this && currentMusicArea.audioSource != null && audioSource != null && currentMusicArea.audioSource.clip == audioSource.clip)
                {
                    AdoptPlaybackFrom(currentMusicArea);
                    currentMusicArea.isPlaying = false;
                    currentMusicArea = this;
                    isPlaying = true;
                    return;
                }

                if (priority > currentMusicArea.priority)
                {
                    currentMusicArea.Exit();
                }
                else
                {
                    return;
                }
            }
            if (audioSource != null)
            {
                audioSource.PlayWithFadeIn(1f, this, volume);
                currentMusicArea = this;
                isPlaying = true;
            }
        }
        public virtual void Exit()
        {
            if (audioSource != null && activeMusicAreas > 0)
            {
                audioSource.PauseWithFadeOut(1f, this, volume);
                if (currentMusicArea == this)
                {
                    currentMusicArea = null;
                }
                isPlaying = false;
            }
        }
        /// <summary>
        /// Return this area to its pre-entry state — no fade, no coroutine, no event —
        /// so that whatever comes next re-enters through the ordinary <see cref="Enter"/>
        /// path instead of inheriting half-torn-down state.
        /// </summary>
        /// <remarks>
        /// Deliberately <see cref="AudioSource.Stop"/> and NOT the 1 s
        /// <c>PauseWithFadeOut</c> that <see cref="Exit"/> uses. A fade is a coroutine
        /// running on this MonoBehaviour, and every caller of StandDown is a moment when
        /// the park is being frozen or torn down around it: entering build mode parks
        /// the world, and OnDisable may be scene teardown, where the coroutine is killed
        /// mid-fade and leaves the source audible-but-orphaned with isPlaying already
        /// false. An audible hard cut the instant you tap Build is the intended
        /// behaviour — a deterministic cut beats a fade that may or may not survive the
        /// frame.
        ///
        /// Idempotent and safe from OnDisable in edit mode: every field is guarded, and
        /// audioSource is null whenever musicTrack was unset (Awake disables the
        /// component and returns before creating the emitter).
        /// </remarks>
        private void StandDown()
        {
            _phoneLevel = 0f;
            _phoneWritten = -1f;
            if (isActive)
            {
                activeMusicAreas--;
                isActive = false;
            }
            if (isPlaying)
            {
                if (audioSource != null)
                {
                    audioSource.Stop();
                }
                if (currentMusicArea == this)
                {
                    currentMusicArea = null;
                }
                isPlaying = false;
            }
        }
        public void SwapAudioClip(AudioClip newClip, float time = 0.5f)
        {
            if (audioSource == null || newClip == null)
                return;

            if (audioSource.clip == newClip)
            {
                musicTrack = newClip;
                return;
            }

            audioSource.PauseWithFadeOut(time, this, volume);
            this.Wait(time, () =>
            {
                audioSource.clip = newClip;
                musicTrack = newClip;
                if (isPlaying)
                {
                    audioSource.PlayWithFadeIn(time, this, volume);
                }
            });
        }
        private void AdoptPlaybackFrom(MusicArea otherArea)
        {
            if (otherArea == null || otherArea.audioSource == null || audioSource == null)
                return;

            AudioSource otherSource = otherArea.audioSource;
            audioSource.clip = otherSource.clip;
            musicTrack = otherArea.musicTrack;
            audioSource.pitch = otherSource.pitch;
            audioSource.time = otherSource.time;
            audioSource.volume = otherSource.volume;

            if (otherSource.isPlaying)
                audioSource.Play();

            otherSource.Stop();
            otherSource.volume = 0f;
        }
        bool IsPointWithinBounds(Vector3 point, Transform obj, Vector3 halfExtents)
        {
            if (isFocused) {
                return true;
            }
            // Vector from center to point in world space
            Vector3 toPoint = point - obj.position;

            // Project onto the object’s local axes
            float x = Vector3.Dot(toPoint, obj.right);
            float y = Vector3.Dot(toPoint, obj.up);
            float z = Vector3.Dot(toPoint, obj.forward);

            // Check against half extents (which can come directly from localScale * 0.5f)
            return Mathf.Abs(x) <= halfExtents.x &&
                Mathf.Abs(y) <= halfExtents.y &&
                Mathf.Abs(z) <= halfExtents.z;
        }
        // Anything that disables a MusicArea used to leak the shared state it had
        // claimed: activeMusicAreas stayed incremented and currentMusicArea kept
        // pointing at an area that no longer Updates, so it could never Exit() itself
        // and every other area was blocked by a priority contest against a ghost. The
        // count is a static, so the leak outlives the object. GameArea already stands
        // itself down in OnDisable for exactly this reason; match it.
        void OnDisable()
        {
            StandDown();
        }
        void OnDestroy()
        {
            s_all.Remove(this);
            if (currentMusicArea == this)
            {
                currentMusicArea = null;
            }
        }
        public float GetPitch()
        {
            return audioSource.pitch;
        }
        public void SetPitch(float pitch)
        {
            audioSource.pitch = pitch;
        }

        // ── Ducking under Dreamie's voice (Oct 20, take 3: "hard to tell how Dreamie dialog etc will be compatible with
        //    the music"). The host app sets VoiceDuckUntil (unscaled time) every frame she speaks with audio. Oct 20
        //    (meadow-host): the 09:22 take measured her voice NOT clearly above the bed in 12 of 43 lines — stage music fading
        //    in as a stop starts, the King Jubbo stinger, Results — so the duck is no longer this area's alone: EVERY playing
        //    AudioSource (a title's Lua stinger, a bookend sting, the round change) eases down under her voice
        //    (<see cref="VoiceDucker"/>). A multiplier on top of whatever the source's own fades write: the un-ducked level
        //    is re-read whenever something else set the volume, so a fade-in ramps UNDER the duck instead of overwriting it.
        /// Duck while Time.unscaledTime is before this (the speaker refreshes it every frame; it lapses on its own).
        public static float VoiceDuckUntil
        {
            get => s_voiceDuckUntil;
            set { s_voiceDuckUntil = value; if (VoiceDucking) VoiceDucker.Ensure(); }
        }
        static float s_voiceDuckUntil = -1f;
        /// Music under her voice (× its own volume): music areas, loops, stingers / jingles (clips ≥ 2.5 s). −14 dB — the
        /// beatcheck target is her voice ≥ 6 dB over the bed in every line.
        public static float VoiceDuckGain = 0.2f;   // 0.25 left the King Jubbo boss-clear pile-up (stinger + fanfare + jingle) at +5.9 dB
        /// Every other playing sound (gameplay SFX) under her voice: −10.5 dB. Measured (meadow-host, Oct 20): at 0.6 her stop
        /// how-to lines over a kick fight / the hunt's start burst sat at +0.7 / +2.7 dB; at 0.35 the hunt start was +5.4.
        public static float VoiceDuckSfxGain = 0.3f;
        /// Ease time (s) down / back up.
        public static float VoiceDuckEaseS = 0.2f;
        /// A host's own voice sources (never ducked). Sources on an object with a DreamieActor are always exempt.
        public static Func<AudioSource, bool> VoiceDuckExempt;
        public static bool VoiceDucking => Time.unscaledTime < s_voiceDuckUntil;

        /// The one ducker: every playing, non-exempt AudioSource eases to its gain while she speaks and back after.
        sealed class VoiceDucker : MonoBehaviour
        {
            sealed class Row { public float duck = 1f, baseVol = -1f, applied = -1f; public bool music; }
            static VoiceDucker s_on;
            static readonly HashSet<string> s_told = new HashSet<string>();
            readonly Dictionary<AudioSource, Row> _rows = new Dictionary<AudioSource, Row>();
            readonly List<AudioSource> _gone = new List<AudioSource>();

            public static void Ensure()
            {
                if (s_on != null || !Application.isPlaying) return;
                var go = new GameObject("[VoiceDucker]") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                s_on = go.AddComponent<VoiceDucker>();
            }

            static bool Exempt(AudioSource a)
            {
                if (a.mute || a.GetComponent("DreamieActor") != null) return true;
                try { return VoiceDuckExempt != null && VoiceDuckExempt(a); } catch (Exception) { return false; }
            }

            // Music-class: loops, long clips, music areas, and clip-less PlayOneShot sources (bookend / UI stings: the Results
            // room's entrance sting under "King Jubbo is stuffed!" kept that line at +5 dB as an SFX).
            static bool IsMusic(AudioSource a) =>
                a.loop || a.clip == null || a.clip.length >= 2.5f || a.GetComponentInParent<MusicArea>() != null;

            void Scan()
            {
                foreach (var a in FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (a == null || !a.isActiveAndEnabled || !a.isPlaying || _rows.ContainsKey(a) || Exempt(a)) continue;
                    // A sound that STARTS under her voice starts ducked (a short tick or kick would be over before an ease).
                    var r = new Row { music = IsMusic(a) };
                    if (a.time < 0.3f) r.duck = r.music ? VoiceDuckGain : VoiceDuckSfxGain;   // already playing: eases down
                    _rows[a] = r;
                    string what = (a.clip != null ? a.clip.name : "one-shot") + " on " + a.gameObject.name;
                    if (s_told.Count < 200 && s_told.Add(what)) Debug.Log($"[VoiceDuck] {(r.music ? "music" : "sfx")} {what}");
                }
            }

            void LateUpdate()
            {
                bool ducking = VoiceDucking;
                if (ducking) Scan();   // every frame while she speaks (only then): new one-shots are caught on their first frame
                if (_rows.Count == 0) return;
                float step = Time.unscaledDeltaTime / Mathf.Max(0.01f, VoiceDuckEaseS);
                _gone.Clear();
                foreach (var kv in _rows)
                {
                    var a = kv.Key; var r = kv.Value;
                    if (a == null) { _gone.Add(a); continue; }
                    if (r.baseVol < 0f || !Mathf.Approximately(a.volume, r.applied)) r.baseVol = a.volume;   // someone else set it
                    float target = ducking ? (r.music ? VoiceDuckGain : VoiceDuckSfxGain) : 1f;
                    r.duck = Mathf.MoveTowards(r.duck, target, step);
                    if (!ducking && Mathf.Approximately(r.duck, 1f)) { a.volume = r.baseVol; _gone.Add(a); continue; }
                    r.applied = Mathf.Clamp01(r.baseVol * r.duck);
                    a.volume = r.applied;
                }
                foreach (var a in _gone) _rows.Remove(a);
            }
        }
        public void Duck(float duckDuration, float fadeInDuration = 0.25f, float fadeOutDuration = 0.25f, float duckVolume = 0.2f)
        {
            if (duckCoroutine != null)
            {
                StopCoroutine(duckCoroutine);
            }
            duckCoroutine = StartCoroutine(DuckCoroutine(duckDuration, fadeInDuration, fadeOutDuration, duckVolume));
        }
        private IEnumerator DuckCoroutine(float duckDuration, float fadeInDuration, float fadeOutDuration, float duckVolume)
        {
            float originalVolume = volume;
            float targetVolume = originalVolume * duckVolume;
            while (audioSource.volume > targetVolume)
            {
                audioSource.volume = Mathf.MoveTowards(audioSource.volume, targetVolume, Time.deltaTime / fadeOutDuration);
                yield return null;
            }
            yield return new WaitForSeconds(duckDuration);
            while (audioSource.volume < originalVolume)
            {
                audioSource.volume = Mathf.MoveTowards(audioSource.volume, originalVolume, Time.deltaTime / fadeInDuration);
                yield return null;
            }
            duckCoroutine = null;
        }
#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            if (!TryGetComponent(out LevelTemplate levelTemplate))
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(transform.position, transform.localScale);
            }
        }
#endif
    }

}
