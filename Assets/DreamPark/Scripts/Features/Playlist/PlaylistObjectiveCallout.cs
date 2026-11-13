// ─────────────────────────────────────────────────────────────────────
//  MOVING TO SAL; NOT PLATFORM (PLAYLIST-PLAN § 6.1.5 / § 6.3.5, Oct 21 2026): games own their instruction UI. Kept until
//  SuperAdventureLand ports this card code into SAL; the platform shows no objective cards once pl-sets switches
//  off the PlaylistObjectiveCallout call sites. Don't add features here.
//  PlaylistObjectiveCallout.cs — what a HEADSET player sees about a stop's
//  goal (Docs/PLAYLISTS.md § 7.4). Soft fades only (Aidan's VR rule: nothing
//  pops, the world never glides), nothing screen-space:
//    • INTRO card (the idle stop's shared ≤ 3 s intro) and BOSS reveal: in FRONT
//      of the player, body-locked with a lazy follow; title (the stop / "BOSS:
//      …"), the goal, then 3 · 2 · 1 · GO! bursting in on the same card.
//    • PERSONAL card (arrival at a live stop, a replay, a re-explain): title,
//      goal, "You're in — GO!" then "Your team: 3/7 coins". Nothing waits.
//    • MARKER: a low-key goal line over a live stop, re-readable any time.
//  This class owns placement + timing; the LOOK is DreamPark Magic UI (MagicCardView
//  over the shared MagicBoard: glowing feathered glass, shimmer, light-trace brand
//  text, countdown bursts, the readability x-ray). The text is re-read every frame.
// ─────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

namespace DreamPark
{
    public enum PlaylistCardKind { Intro, Boss, Personal, Marker }

    /// One card's visuals (Magic UI by default; a plain glowing-text fallback).
    public interface IPlaylistCardView
    {
        /// Every frame; a view ignores unchanged strings. A short headline ("3", "2", "1", "GO!") is a countdown beat.
        void Show(string title, string headline, string foot);
        /// The card's fade (0..1) — a Magic board draws itself in and dissolves on Leave, so it may ignore it.
        void SetFade(float alpha);
        /// Readability, EVERY FRAME while it shows: from the eye, the card at <paramref name="desired"/> (facing the eye,
        /// <paramref name="right"/> = its right axis). Returns where the card should stand: the desired spot when every
        /// corner is clear, else the clearest nearby spot (closer / higher / to a side); the view draws over geometry
        /// when even that is blocked.
        Vector3 Place(Vector3 eye, Vector3 desired, Vector3 right);
        /// After the card moved this frame: the final draw-over decision at its actual pose.
        void Seen(Vector3 eye);
        /// The share (0..1) of eye → card rays clear right now, counting the draw-over (1 when drawn over) — the
        /// automated readability check.
        float Visible { get; }
        /// Leave (soft): a dissolve; <see cref="Gone"/> once it has.
        void Leave();
        bool Gone { get; }
    }

    public sealed class PlaylistObjectiveCallout : MonoBehaviour
    {
        public const float FadeIn = 0.45f, FadeOut = 0.6f;
        public const float CardDistance = 1.15f, CardDrop = 0.12f;   // metres in front of / below the eyes
        public const float RecentreDegrees = 28f, FollowSharpness = 2.5f;
        /// The card's far edge never sits wider than this from the wearer's straight-ahead (inside the headset's
        /// binocular view): a lazy-follow lag or a side-stepped spot is swung back in, so no corner is ever off-screen.
        public const float ViewEdgeDegrees = 40f;
        public static readonly Color Violet = new Color(0.62f, 0.42f, 1f, 1f);
        public static readonly Color Face = new Color(0.97f, 0.94f, 1f, 1f);
        public const float MarkerAlpha = 0.45f;

        /// The player's head (the content camera by default; harnesses may replace it).
        public static Func<Transform> Head = () => Camera.main != null ? Camera.main.transform : null;
        /// True = hold the stop intros off (another moment owns the stage: the round diorama). Arrivals wait, then play.
        public static Func<bool> HoldOff;
        /// The look override. Null = DreamPark Magic UI (<see cref="MagicCardView"/>, the bookends' shared kit).
        public static Func<Transform, PlaylistCardKind, IPlaylistCardView> Skin;

        PlaylistCardKind _kind;
        Func<string> _headline, _goal, _footer;
        Func<bool> _alive;
        string _variant;
        double _t0, _personalSeconds;
        Func<double> _clock;
        Transform _anchor;
        IPlaylistCardView _view;
        Vector3 _pos;
        float _yaw;
        bool _placed, _recentring;
        float _born, _dyingAt = -1f;

        /// The readability check of this card right now (1 = fully readable; see IPlaylistCardView.Visible).
        public float Visible => _view != null ? _view.Visible : 1f;
        public PlaylistCardKind Kind => _kind;
        public IPlaylistCardView View => _view;

        IPlaylistCardView MakeView(PlaylistCardKind kind)
        {
            IPlaylistCardView v = null;
            try { v = (Skin ?? MagicCardView.Create)(transform, kind); }
            catch (Exception e) { Debug.LogWarning("[Playlist] card look: " + e.Message); }
            return v ?? GlowTextView.Create(transform, kind);   // the plain fallback if Magic UI cannot build
        }

        /// The shared idle-stop intro card in front of this player: <paramref name="variant"/> + <paramref name="startedAt"/>
        /// (shared clock) drive the 3·2·1 (PlaylistStopIntroRules.PhaseAt).
        public static PlaylistObjectiveCallout Intro(string variant, double startedAt, Func<double> clock,
            Func<string> headline, Func<string> goal)
            => Make(variant == PlaylistStopIntroRules.Boss ? PlaylistCardKind.Boss : PlaylistCardKind.Intro,
                variant, startedAt, clock, headline, goal, null, 0);

        /// A PERSONAL card (late arrival, a live stop, a replay, a re-explain): no countdown, nothing waits.
        public static PlaylistObjectiveCallout Personal(string variant, Func<double> clock, Func<string> headline,
            Func<string> goal, Func<double, string> footer)
        {
            double t0 = clock();
            return Make(variant == PlaylistStopIntroRules.Boss ? PlaylistCardKind.Boss : PlaylistCardKind.Personal,
                variant, t0, clock, headline, goal, () => footer(clock() - t0), PlaylistStopIntroRules.PersonalSeconds(variant));
        }

        static PlaylistObjectiveCallout Make(PlaylistCardKind kind, string variant, double startedAt, Func<double> clock,
            Func<string> headline, Func<string> goal, Func<string> footer, double personalSeconds)
        {
            // ONE objective card at a time (F23, Oct 20 take 3: Jubbo Crossing + Brick Stacks both up showing GO/3): the
            // newest card is the current target's, so any stop card still up dissolves as it comes in. Markers stay.
            for (int i = s_all.Count - 1; i >= 0; i--)
            {
                var old = s_all[i];
                if (old == null || old._kind == PlaylistCardKind.Marker || old._dyingAt >= 0) continue;
                Debug.Log($"[Playlist] card: '{Safe(old._headline)}' steps back for '{Safe(headline)}' (one card at a time)");
                old.Dismiss();
            }
            var go = new GameObject(footer != null ? "PlaylistStopCard" : "PlaylistStopIntro");
            var c = go.AddComponent<PlaylistObjectiveCallout>();
            c._kind = kind; c._variant = variant; c._t0 = startedAt; c._clock = clock;
            c._headline = headline; c._goal = goal; c._footer = footer; c._personalSeconds = personalSeconds;
            c._born = Time.time;
            c.Follow(true);
            c._view = c.MakeView(kind);
            return c;
        }

        /// A low-key marker over a stop while <paramref name="alive"/> holds.
        public static PlaylistObjectiveCallout Marker(GameObject stopRoot, Func<string> goal, Func<bool> alive, float height = 1.7f)
        {
            if (stopRoot == null) return null;
            var go = new GameObject("PlaylistGoalMarker");
            var c = go.AddComponent<PlaylistObjectiveCallout>();
            c._kind = PlaylistCardKind.Marker; c._anchor = stopRoot.transform; c._goal = goal; c._alive = alive;
            c._height = height;
            c._pos = MarkerOffset(stopRoot.transform, height);
            c._born = Time.time;
            c.transform.position = stopRoot.transform.position + c._pos;
            c._view = c.MakeView(PlaylistCardKind.Marker);
            return c;
        }

        float _height = 1.7f, _nextMeasure;

        static readonly System.Collections.Generic.List<PlaylistObjectiveCallout> s_all = new System.Collections.Generic.List<PlaylistObjectiveCallout>();
        void OnEnable() { if (!s_all.Contains(this)) s_all.Add(this); }
        void OnDisable() => s_all.Remove(this);
        /// A stop's own card (intro / GO! / goal / boss) is up: the field's other labels (bonus) step back from it.
        public static bool AnyCardUp
        {
            get { foreach (var c in s_all) if (c != null && c._kind != PlaylistCardKind.Marker && c._dyingAt < 0) return true; return false; }
        }

        /// Clear air between the marker and the attraction's highest point, and the marker's floor-relative minimum
        /// (above any player's head).
        public const float MarkerClearance = 0.35f, MarkerMinHeight = 2.3f, MarkerMaxHeight = 3.4f;

        /// The marker's offset from its stop root: over the attraction's bounds centre, above its top (props included,
        /// particles / UI / below-floor content not), never below head height.
        public static Vector3 MarkerOffset(Transform root, float minHeight)
        {
            if (root == null) return Vector3.up * Mathf.Max(minHeight, MarkerMinHeight);
            Bounds b = default; bool any = false;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (r.GetComponentInParent<PlaylistObjectiveCallout>() != null) continue;
                if (r.bounds.size.sqrMagnitude > 2500f) continue;   // a sky / far backdrop
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            float floor = root.position.y;
            if (!any) return Vector3.up * Mathf.Max(minHeight, MarkerMinHeight);
            float y = Mathf.Clamp(b.max.y + MarkerClearance, floor + Mathf.Max(minHeight, MarkerMinHeight), floor + MarkerMaxHeight);
            return new Vector3(b.center.x, y, b.center.z) - root.position;
        }

        /// The marker fades away as the viewer comes in under it (2.2 m → 1.2 m, horizontal): up close the personal
        /// card owns the goal.
        public static float MarkerNearAlpha(Vector3 marker, Vector3 eye)
        {
            float d = new Vector2(marker.x - eye.x, marker.z - eye.z).magnitude;
            return Smooth((d - 1.2f) / 1.0f);
        }

        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        /// Lazy body-lock: stay where it is until the head turns RecentreDegrees away, then ease back in front.
        void Follow(bool snap)
        {
            var head = Head();
            if (head == null) return;
            Vector3 f = head.forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            float headYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            if (snap || !_placed) { _yaw = headYaw; _placed = true; }
            else if (_recentring || Mathf.Abs(Mathf.DeltaAngle(_yaw, headYaw)) > RecentreDegrees)
            {
                _recentring = Mathf.Abs(Mathf.DeltaAngle(_yaw, headYaw)) > 2f;
                _yaw = Mathf.LerpAngle(_yaw, headYaw, 1f - Mathf.Exp(-FollowSharpness * Time.deltaTime));
            }
            Vector3 dir = Quaternion.Euler(0, _yaw, 0) * Vector3.forward;
            Vector3 target = head.position + dir * CardDistance + Vector3.down * CardDrop;
            // Readability first: a spot where nothing of the attraction cuts the card (else it draws over).
            if (_view != null) target = _view.Place(head.position, target, Quaternion.Euler(0, _yaw, 0) * Vector3.right);
            transform.position = snap ? target : Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-FollowSharpness * 2f * Time.deltaTime));
            // Walking never closes in on it (Oct 19 dry runs: the eased card lagged a walking player to 0.89 m and
            // covered a third of the view): along the gaze it is never nearer than its distance.
            float along = Vector3.Dot(transform.position - head.position, dir);
            if (along < CardDistance - 0.03f) transform.position += dir * (CardDistance - 0.03f - along);
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            KeepInView(head.position, headYaw);
            _view?.Seen(head.position);
        }

        void KeepInView(Vector3 eye, float headYaw)
        {
            Vector3 to = transform.position - eye; to.y = 0f;
            float dist = to.magnitude;
            if (dist < 0.2f) return;
            float half = _view is MagicCardView m ? m.Width * 0.5f : 0.4f;
            float limit = Mathf.Max(0f, ViewEdgeDegrees - Mathf.Atan2(half, dist) * Mathf.Rad2Deg);
            float delta = Mathf.DeltaAngle(headYaw, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
            if (Mathf.Abs(delta) <= limit) return;
            Quaternion swing = Quaternion.Euler(0f, Mathf.Sign(delta) * limit - delta, 0f);
            transform.position = eye + swing * (transform.position - eye);
            transform.rotation = swing * transform.rotation;
            float lag = Mathf.DeltaAngle(headYaw, _yaw);
            if (Mathf.Abs(lag) > limit) _yaw = headYaw + Mathf.Sign(lag) * limit;
        }

        /// Fade out (soft) and go.
        public void Dismiss()
        {
            if (_dyingAt >= 0) return;
            _dyingAt = Time.time;
            try { _view?.Leave(); } catch (Exception) { }
        }

        static string Safe(Func<string> f) { try { return f?.Invoke(); } catch (Exception) { return null; } }

        void LateUpdate()
        {
            float fadeIn = Smooth((Time.time - _born) / FadeIn);
            float fadeOut = _dyingAt < 0 ? 1f : 1f - Smooth((Time.time - _dyingAt) / FadeOut);
            if (_dyingAt >= 0 && Time.time - _dyingAt >= FadeOut && (_view == null || _view.Gone || Time.time - _dyingAt > 1.5f))
            { Destroy(gameObject); return; }
            string goal = Safe(_goal) ?? "";
            var head = Head();

            if (_kind == PlaylistCardKind.Marker)
            {
                bool alive = _anchor != null && _anchor.gameObject.activeInHierarchy;
                try { alive = alive && (_alive == null || _alive()); } catch (Exception) { alive = false; }
                if (!alive) Dismiss();
                float near = 1f;
                if (_anchor != null && _anchor.gameObject.activeInHierarchy)
                {
                    // Over the attraction's middle and clear of its highest point — never at eye height where a player
                    // walking in would meet it face-first (Oct 19 recut: the goal text filled the view from 0.1 m).
                    if (Time.time >= _nextMeasure) { _nextMeasure = Time.time + 2f; _pos = MarkerOffset(_anchor, _height); }
                    transform.position = _anchor.position + _pos;
                    if (head != null) near = MarkerNearAlpha(transform.position, head.position);
                    // At a locked field the lock speaks: other stops' goals step back (Oct 19 dry 11 pile-up).
                    if (head != null && PlaylistLockField.NearLocked(head.position, 3f)) near = 0f;
                    if (head != null)
                    {
                        Vector3 d = transform.position - head.position; d.y = 0f;
                        if (d.sqrMagnitude > 1e-4f)
                        {
                            transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
                            // The marker stays over its stop; it only draws over when something cuts it.
                            _view?.Place(head.position, transform.position, transform.right);
                            _view?.Seen(head.position);
                        }
                    }
                }
                _view?.Show("", goal, "");
                _view?.SetFade(MarkerAlpha * fadeIn * fadeOut * near);
                return;
            }

            Follow(false);
            string title = Safe(_headline) ?? "";
            double t = _clock() - _t0;
            if (_footer != null)
            {
                _view?.Show(title, goal, Safe(_footer) ?? "");
                _view?.SetFade(fadeIn * fadeOut);
                if (t >= _personalSeconds) Dismiss();
                return;
            }
            string phase = PlaylistStopIntroRules.PhaseAt(_variant, t, out int count);
            // The goal first; then the count bursts in where the goal was, with the goal kept underneath.
            if (phase == PlaylistStopIntroRules.PhaseCard) _view?.Show(title, goal, "");
            else if (phase == PlaylistStopIntroRules.PhaseCountdown) _view?.Show(title, count.ToString(), goal);
            else _view?.Show(title, "GO!", goal);
            _view?.SetFade(fadeIn * fadeOut);
            if (phase == PlaylistStopIntroRules.PhaseDone) Dismiss();
        }
    }
}
