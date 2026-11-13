namespace DreamPark
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>What the phone is looking at, for its music (the host sets it every frame; see <see cref="MusicAreaPhone"/>).</summary>
    public enum MusicView
    {
        /// Nothing of the park is being played or watched (staging, another tab): no game music.
        Off,
        /// Full-screen AR ("playing AR": the sheet pulled down / the camera button): the phone's own camera is the
        /// listener's place in the park.
        Ar,
        /// The map view (the diorama, the live park through its own camera; the phone's default): the music follows what
        /// the map is about.
        Diorama,
    }

    /// <summary>The host's answer for this frame.</summary>
    public struct MusicListen
    {
        public MusicView view;
        /// Diorama only: the park-world points the diorama is about (the tour's stop, the headsets' heads). Empty = none.
        public IList<Vector3> focus;
        /// Level on top of each area's own volume (1 = as authored; the diorama and the scan's shimmer sit it lower).
        public float gain;
    }

    /// <summary>One music area, as the pure rule sees it.</summary>
    public struct MusicCandidate
    {
        public int id;
        public Vector3 center, right, up, forward;
        public Vector3 halfExtents;
        public int priority;
        /// It has a track to play (a title with its own music director clears the tracks: it still has a level to follow).
        public bool hasTrack;
        /// EasyMusicArea focus (a boss fight's music): wins wherever the listener is.
        public bool focused;

        public float FootprintArea => 4f * halfExtents.x * halfExtents.z;

        /// How far <paramref name="p"/> is outside the footprint, in metres (0 = inside).
        public float Outside(Vector3 p)
        {
            Vector3 d = p - center;
            float x = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, right)) - halfExtents.x);
            float y = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, up)) - halfExtents.y);
            float z = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, forward)) - halfExtents.z);
            return Mathf.Sqrt(x * x + y * y + z * z);
        }
    }

    /// <summary>
    /// PHONE MUSIC (Oct 23 2026, phone-music). Which music area a phone plays — pure, no scene access
    /// (Tools/phone-music-offline, MusicAreaTests).
    ///
    /// A headset hears an area's music when its head walks into the area. A phone has no rig (it is a spectator unless it
    /// wears a title's Phone Player) and the AR-first phone is build-parked for the whole session, which stands every
    /// MusicArea down — so a phone never heard game music. On a phone the platform picks ONE area:
    ///  * AR: the area the phone's camera is in (most specific first), else the nearest within <see cref="NearMeters"/>,
    ///    else none (the app's title theme covers);
    ///  * diorama: the area holding the most focus points (the tour's stop, the headsets), else the nearest within
    ///    <see cref="NearMeters"/> of one, else the one already playing, else the park's main area (highest priority,
    ///    then the biggest footprint) — the diorama always has music when any area has a track;
    ///  * an EasyMusicArea-focused area wins in both.
    /// </summary>
    public static class MusicAreaPhoneRules
    {
        /// "Nearby": a phone standing in a doorway still hears the room it is looking into.
        public const float NearMeters = 2.5f;
        /// Under the game's SFX in the diorama (−6 dB).
        public const float DioramaGain = 0.5f;
        /// The host's gain for a view. Oct 23 (Aidan: the shimmer "is the loudest thing"): the scan / resync shimmer never
        /// lowers the music any more — the SHIMMER ducks under the music (core ScanMagicAudio). <paramref name="scanSinging"/>
        /// is kept for callers and ignored.
        public static float GainFor(MusicView view, bool scanSinging)
        {
            if (view == MusicView.Off) return 0f;
            return view == MusicView.Diorama ? DioramaGain : 1f;
        }

        /// <param name="needTrack">true = only areas with a track (what plays); false = any area (the level a title's own music follows)</param>
        /// <returns>the chosen candidate's id, or -1</returns>
        public static int Pick(MusicView view, IList<MusicCandidate> all, Vector3 camera, IList<Vector3> focus,
            int currentId, bool needTrack)
        {
            if (view == MusicView.Off || all == null || all.Count == 0) return -1;
            int best = -1;
            // A focused area (EasyMusicArea: the boss's music) wins anywhere.
            for (int i = 0; i < all.Count; i++)
            {
                MusicCandidate c = all[i];
                if (!c.focused || (needTrack && !c.hasTrack)) continue;
                if (best < 0 || c.priority > all[best].priority) best = i;
            }
            if (best >= 0) return all[best].id;

            int pointCount = view == MusicView.Ar ? 1 : (focus != null ? focus.Count : 0);
            if (pointCount > 0)
            {
                // Inside: the most points, then priority, then the one already playing, then the most specific (smallest).
                int bestHits = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    MusicCandidate c = all[i];
                    if (needTrack && !c.hasTrack) continue;
                    int hits = 0;
                    for (int k = 0; k < pointCount; k++)
                        if (c.Outside(view == MusicView.Ar ? camera : focus[k]) <= 0f) hits++;
                    if (hits == 0) continue;
                    if (best < 0 || Better(c, hits, all[best], bestHits, currentId)) { best = i; bestHits = hits; }
                }
                if (best >= 0) return all[best].id;
                // Nearby: the closest edge within NearMeters.
                float bestD = float.MaxValue;
                for (int i = 0; i < all.Count; i++)
                {
                    MusicCandidate c = all[i];
                    if (needTrack && !c.hasTrack) continue;
                    float d = float.MaxValue;
                    for (int k = 0; k < pointCount; k++) d = Mathf.Min(d, c.Outside(view == MusicView.Ar ? camera : focus[k]));
                    if (d > NearMeters) continue;
                    if (best < 0 || d < bestD - 0.01f || (Mathf.Abs(d - bestD) <= 0.01f && c.priority > all[best].priority))
                    { best = i; bestD = d; }
                }
                if (best >= 0) return all[best].id;
            }
            if (view == MusicView.Ar) return -1;

            // Diorama, nothing in focus: keep what plays, else the park's main area.
            for (int i = 0; i < all.Count; i++)
                if (all[i].id == currentId && (!needTrack || all[i].hasTrack)) return currentId;
            for (int i = 0; i < all.Count; i++)
            {
                MusicCandidate c = all[i];
                if (needTrack && !c.hasTrack) continue;
                if (best < 0) { best = i; continue; }
                MusicCandidate b = all[best];
                if (c.priority != b.priority) { if (c.priority > b.priority) best = i; continue; }
                float ca = c.FootprintArea, ba = b.FootprintArea;
                if (Mathf.Abs(ca - ba) > 0.01f) { if (ca > ba) best = i; continue; }
                if (c.id < b.id) best = i;
            }
            return best >= 0 ? all[best].id : -1;
        }

        static bool Better(MusicCandidate c, int hits, MusicCandidate b, int bHits, int currentId)
        {
            if (hits != bHits) return hits > bHits;
            if (c.priority != b.priority) return c.priority > b.priority;
            if ((c.id == currentId) != (b.id == currentId)) return c.id == currentId;
            float ca = c.FootprintArea, ba = b.FootprintArea;
            if (Mathf.Abs(ca - ba) > 0.01f) return ca < ba;
            return c.id < b.id;
        }
    }

    /// <summary>
    /// The phone's music director (Oct 23 2026, phone-music): the runtime half of <see cref="MusicAreaPhoneRules"/>.
    /// Active only on a phone whose host app set <see cref="Source"/>; a headset never runs it (MusicArea's head-in-area
    /// path is untouched there). Once per frame it gathers every live MusicArea, picks the one the phone plays
    /// (<see cref="Pick"/>) and the level a title's own music director follows (<see cref="Focus"/>, Lua
    /// dp.music_focus()). MusicArea.Update does the playing; the host reports "game music audible" to the app.
    /// </summary>
    public static class MusicAreaPhone
    {
        /// The host app's view of the phone (null = not driven: the SDK's own rule).
        public static Func<MusicListen> Source;

        /// Kill switch (the host may also clear <see cref="Source"/>).
        public static bool Enabled = true;
        /// The phone path is on (a phone, and a host driving it, and it has never faulted this session).
        public static bool Active => Enabled && !s_faulted && Source != null && DreamParkDevice.IsPhone;

        static bool s_faulted;
        /// Any exception on the phone music path turns it off for the session (logged once): music is presentation,
        /// it must never take the app down or spam the log every frame.
        public static void Fault(string where, Exception e)
        {
            if (s_faulted) return;
            s_faulted = true;
            Pick = null; Focus = null;
            Debug.LogError("[MusicAreaPhone] phone music OFF for this session — " + where + ": " + e);
        }
        public static bool Faulted => s_faulted;

        public static MusicView View { get; private set; }
        /// The area the phone plays (has a track), or null.
        public static MusicArea Pick { get; private set; }
        /// The level the phone's music follows (any area, with or without a track), or null.
        public static MusicArea Focus { get; private set; }
        /// Level on top of each area's own volume.
        public static float Gain { get; private set; } = 1f;

        static int s_frame = -1;
        static readonly List<MusicCandidate> s_cands = new List<MusicCandidate>();
        static readonly List<MusicArea> s_areas = new List<MusicArea>();
        static readonly Vector3[] s_noFocus = new Vector3[0];

        /// Once per frame (every MusicArea's Update calls it; the first one does the work).
        public static void Step()
        {
            int frame = Time.frameCount;
            if (frame == s_frame || s_faulted) return;
            s_frame = frame;
            try { StepInner(); }
            catch (Exception e) { Fault("Step", e); }
        }

        static void StepInner()
        {
            MusicListen l = default;
            try { if (Source != null) l = Source(); }
            catch (Exception e) { Debug.LogWarning("[MusicAreaPhone] Source threw: " + e.Message); l = default; }
            View = l.view;
            Gain = Mathf.Clamp01(l.view == MusicView.Off ? 0f : l.gain);
            s_cands.Clear();
            s_areas.Clear();
            IReadOnlyList<MusicArea> all = MusicArea.All;
            for (int i = 0; i < all.Count; i++)
            {
                MusicArea a = all[i];
                if (a == null || !a.gameObject.activeInHierarchy) continue;
                bool hasTrack = a.musicTrack != null && a.HasEmitter;
                if (hasTrack && !a.enabled) continue;   // switched off on purpose (a title's script)
                Transform t = a.transform;
                s_cands.Add(new MusicCandidate
                {
                    id = s_areas.Count, center = t.position, right = t.right, up = t.up, forward = t.forward,
                    halfExtents = a.FootprintHalfExtents(), priority = a.priority, hasTrack = hasTrack, focused = a.isFocused,
                });
                s_areas.Add(a);
            }
            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            if (l.view == MusicView.Ar && cam == null) l.view = MusicView.Off;
            IList<Vector3> focus = l.focus ?? s_noFocus;
            MusicArea playing = s_areas.Count > 0 ? MusicArea.currentMusicArea : null;
            int cur = IndexOf(playing != null ? playing : Pick);
            int pick = MusicAreaPhoneRules.Pick(l.view, s_cands, camPos, focus, cur, needTrack: true);
            int fcur = IndexOf(Focus);
            int foc = MusicAreaPhoneRules.Pick(l.view, s_cands, camPos, focus, fcur, needTrack: false);
            Pick = pick >= 0 ? s_areas[pick] : null;
            Focus = foc >= 0 ? s_areas[foc] : null;
        }

        static int IndexOf(MusicArea a)
        {
            if (a == null) return -1;
            for (int i = 0; i < s_areas.Count; i++) if (s_areas[i] == a) return i;
            return -1;
        }

        // ── A title's own music director (Lua: WW.music) ─────────────────

        /// Lua dp.music_focus(): the level root the phone's music follows; nil off the phone path.
        public static GameObject FocusObject()
        {
            try
            {
                if (!Active) return null;
                Step();
                return Focus != null ? Focus.gameObject : null;
            }
            catch (Exception e) { Fault("dp.music_focus", e); return null; }
        }

        /// Lua dp.music_gain(): what a title's own music multiplies its level by (1 off the phone path).
        public static float LuaGain()
        {
            try
            {
                if (!Active) return 1f;
                Step();
                return Gain;
            }
            catch (Exception e) { Fault("dp.music_gain", e); return 1f; }
        }

        /// Lua dp.music_playing(on): a title's own music director says its bed is (not) sounding. A lease: it lapses
        /// <see cref="TitleMusicLeaseSeconds"/> after the last true, so a director that stops ticking never holds it.
        public static void ReportTitleMusic(bool on)
        {
            s_titleMusicUntil = on ? Time.unscaledTime + TitleMusicLeaseSeconds : -1f;
        }
        public const float TitleMusicLeaseSeconds = 1f;
        static float s_titleMusicUntil = -1f;
        public static bool TitleMusicPlaying => Time.unscaledTime < s_titleMusicUntil;

        /// The game's music is sounding on this device now: the picked area's emitter is playing, unmuted, above a
        /// whisper — or a title's director holds the lease. (The listener's own pause/volume is the host's to add.)
        public static bool GameMusicSounding(out string by)
        {
            by = null;
            MusicArea a = MusicArea.All.Count > 0 ? MusicArea.currentMusicArea : null;
            if (a != null && a.isPlaying && a.EmitterSounding)
            {
                by = "area:" + a.name;
                return true;
            }
            if (TitleMusicPlaying) { by = "title"; return true; }
            return false;
        }

        /// Tests / harnesses.
        public static void ResetForTests()
        {
            Source = null; s_frame = -1; Pick = null; Focus = null; Gain = 1f; View = MusicView.Off; s_titleMusicUntil = -1f;
            s_faulted = false; Enabled = true;
        }
    }
}
