using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    // ─────────────────────────────────────────────────────────────────────
    //  DreamParkParticipation.cs — the rig is the gate (Oct 23 2026, platform-rig).
    //
    //  Aidan's model: a device takes part in a title's gameplay only while it
    //  WEARS that title's rig — a headset its Player rig, a phone the title's
    //  Phone Player (.dreampark-phone-player.json), which the phone wears only
    //  in camera / full-screen AR (PhoneRigGate in core). A device without the
    //  rig is a SPECTATOR: it watches everything live, but it cannot change
    //  anyone's game.
    //
    //  What a spectator cannot do (enforced by the platform):
    //    lead      dp.leader() / dp.rank() / the SharedState writer skip it
    //              (SessionEngine.Participates, Gather, SharedState.LeaderId);
    //    write     its SharedState writes / requests / merge cells on a title's
    //              tables are dropped at the source AND refused by the writer;
    //    send      net_send on a title's objects is a no-op, except ASKS (a
    //              kind ending _req / _request / _rq / _hi / _hello: "send me
    //              the state"), so its view still catches up;
    //    publish   dp.set_state (it has no player).
    //  Playlist events, dp.event, stop.* signals and dp.playlist.score /
    //  time_limit / signal all travel as SharedState requests on the run's
    //  tables, so the writer gate covers them.
    //
    //  ALWAYS ALLOWED (Aidan, Oct 23): the phone's control of the PLAYLIST and
    //  QUEUE — Play / switch / Add to queue / skip / stop go through the Web
    //  queue (Swift) and the phone's own pack + Location save, which this gate
    //  never touches; every headset at the Location follows the queue. Inside
    //  Unity the platform allow-lists the tables those controls use: the hold
    //  board's What's Next picks (c.*, final, next), the runner's "begin" (the
    //  start circle) and Dreamie moments ("dm"). Phone TAPS are deliberate
    //  actions: everything a tap handler does runs as a spectator action (the
    //  SAL start coin of a hunt, the start circle). Title Lua marks a deferred
    //  deliberate action with dp.spectator_action(fn, ...).
    //
    //  Lua: dp.participating([go|title]) — true on a headset, and on a phone
    //  only while it wears the title's rig. Scripts that run on every device
    //  (Game Manager / Game Container) skip gameplay work when it is false.
    //
    //  LIVE CAMERA (Nov 13 2026; DreamParkDevice.IsCamera, set by core from
    //  Camera Mode): a headset that is a portal's live camera is a spectator
    //  of EVERY title, rig or not — it watches and records, never plays. Same
    //  gates as a phone without its rig; it takes no deliberate actions (no
    //  taps), and its session beat says "phone" + "cm" so the room agrees.
    // ─────────────────────────────────────────────────────────────────────
    public static class DreamParkParticipation
    {
        [ThreadStatic] static int s_scope;

        /// <summary>Inside a deliberate spectator action (a phone tap, dp.spectator_action): the gates let it through.</summary>
        public static bool InSpectatorAction => s_scope > 0;

        public static void EnterSpectatorAction() { s_scope++; }
        public static void ExitSpectatorAction() { if (s_scope > 0) s_scope--; }

        /// <summary>using (DreamParkParticipation.SpectatorAction()) { … } — a deliberate action from a spectator.</summary>
        public static Scope SpectatorAction() { EnterSpectatorAction(); return new Scope(); }
        public struct Scope : IDisposable { public void Dispose() => ExitSpectatorAction(); }

        /// <summary>Console sink (swapped by out-of-Editor test runners).</summary>
        internal static Action<string> Log = m => Debug.Log(m);

        /// <summary>Does THIS device take part in <paramref name="title"/>'s gameplay? Headsets: always. Phones: while wearing its rig. Empty title = any rig.</summary>
        public static bool Local(string title)
        {
            if (DreamParkDevice.IsCamera) return false;   // a live camera never takes part (Nov 13 2026)
            if (!DreamParkDevice.IsPhone) return true;
            return WearsLocal(title);
        }

        /// <summary><see cref="Local(string)"/> for the title of <paramref name="go"/> (null = any rig).</summary>
        public static bool Local(GameObject go) => Local(go != null ? SharedState.TitleOf(go) : null);

        /// <summary>dp.participating(x): x = a GameObject (its title), a title id, or nil (any rig).</summary>
        public static bool Participating(object x)
        {
            if (x is GameObject go) return Local(go);
            if (x is Component c) return Local(c != null ? c.gameObject : null);
            return Local(x as string);
        }

        static bool WearsLocal(string title)
        {
            var rig = PlayerRig.Instance;
            if (rig == null || !rig.gameObject.activeInHierarchy) return false;
            if (string.IsNullOrEmpty(title)) return true;
            // A title and its Beta twin are one title ("WizardsWay" rig ~ "WizardsWayBeta" Game Manager).
            string twin = SessionEngine.BetaTwin(title);
            if (string.Equals(rig.gameId, title, StringComparison.Ordinal) || string.Equals(rig.gameId, twin, StringComparison.Ordinal)) return true;
            return PlayerRig.instances != null
                && ((PlayerRig.instances.TryGetValue(title, out var r) && r == rig) || (PlayerRig.instances.TryGetValue(twin, out var r2) && r2 == rig));
        }

        /// <summary>A read-only ask a spectator may still send so its view catches up ("send me the state").</summary>
        public static bool IsAskKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return false;
            string k = kind.ToLowerInvariant();
            return k.EndsWith("_req", StringComparison.Ordinal) || k.EndsWith("_request", StringComparison.Ordinal)
                || k.EndsWith("_rq", StringComparison.Ordinal) || k.EndsWith("_hi", StringComparison.Ordinal)
                || k.EndsWith("_hello", StringComparison.Ordinal) || k == "hello";
        }

        /// <summary>net_send gate: may THIS device send <paramref name="kind"/> from <paramref name="go"/>?</summary>
        public static bool MaySend(GameObject go, string kind)
        {
            if (IsAskKind(kind)) return true;
            if (!DreamParkDevice.IsPhone && !DreamParkDevice.IsCamera) return true;
            if (InSpectatorAction && !DreamParkDevice.IsCamera) return true;
            string title = go != null ? SharedState.TitleOf(go) : null;
            if (Local(title)) return true;
            NoteBlocked("net_send('" + kind + "')", title, go != null ? go.name : null);
            return false;
        }

        /// <summary>dp.set_state gate: a spectator has no player to publish.</summary>
        public static bool MaySetState()
        {
            if (DreamParkDevice.IsCamera) { NoteBlocked("dp.set_state", null, null); return false; }
            if (!DreamParkDevice.IsPhone || InSpectatorAction || Local((string)null)) return true;
            NoteBlocked("dp.set_state", null, null);
            return false;
        }

        static readonly HashSet<string> s_logged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Logged once per (what, title): a spectator's gameplay write was not sent.</summary>
        internal static void NoteBlocked(string what, string title, string where)
        {
            string key = what + "|" + (title ?? "");
            if (s_logged.Count > 512 || !s_logged.Add(key)) return;
            try
            {
                if (DreamParkDevice.IsCamera)
                    Log($"[Participation] live camera: {what}{(string.IsNullOrEmpty(title) ? "" : " (" + title + ")")}{(string.IsNullOrEmpty(where) ? "" : " from " + where)} not sent — " +
                        "a camera headset watches and records, it never plays. Gate gameplay on dp.participating().");
                else
                Log($"[Participation] spectator (no {(string.IsNullOrEmpty(title) ? "" : title + " ")}rig): {what}{(string.IsNullOrEmpty(where) ? "" : " from " + where)} not sent — " +
                    "a phone takes part only while it wears the title's Phone Player (full-screen AR). Gate gameplay on dp.participating().");
            }
            catch (Exception) { }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_scope = 0; s_logged.Clear(); }
    }
}
