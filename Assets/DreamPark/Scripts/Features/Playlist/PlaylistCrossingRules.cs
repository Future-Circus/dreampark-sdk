// ─────────────────────────────────────────────────────────────────────
//  PlaylistCrossingRules.cs — the pure rules of the two reusable movement challenges (goal-ui, Oct 20 2026; Aidan:
//  "making it from one end to the other without getting hit by Pengo eggs, just the same as lava pit burns", then
//  "jump and crossing are different challenges"). CONTRACT Changes (Oct 20, crossing / jump).
//    • CROSSING  stop.crossing{from='start', to='far', bounds='footprint', fail_on={…}, time=?}
//        waiting (the purple START line shows) → armed at the start line → crossing once past it (the start line fades,
//        the purple FINISH line + flag pop in) → a fail sends the wearer back (finish hides, start shows) → the far
//        edge = done (burst). Events: crossing_start{player}, crossing_fail{player, why, cause?, fails},
//        crossing_done{player, fails}.
//    • JUMP      stop.jump{pads={posA, posB, …}, window=2.5, fail_on={…}}  (jump pads)
//        pad A shows first; standing in pad k pops in the arch + pad k+1; a hop = leave pad k and land inside pad k+1
//        within `window` s with no fail; a fail returns the wearer to pad k. Events: jump_hop{player, from, to},
//        jump_fail{player, why, cause?, fails}, jump_done{player, fails}.
//    • fail_on entries: 'hazard' (the head over lava / a cutout / a hazard), 'out' (left the footprint), 'fall' (no
//      floor under the head), {hit={cause=…}} or 'hit' (the title's hit{cause} on this wearer, filtered by cause),
//      {event=name} (any title event on this wearer), {time=N} (the attempt fails after N s).
//  Per LOCAL wearer: every device runs its own wearer's attempt; the leader's goal block only sees the events.
//  Engine-free (offline sim: Tools/playlist-run-offline).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;

namespace DreamPark
{
    /// What fails an attempt (parsed from fail_on).
    public sealed class PlaylistFailOn
    {
        public bool hazard, @out, fall, anyHit;
        public readonly List<string> hitCauses = new List<string>();
        public readonly List<string> events = new List<string>();
        public double time;   // 0 = none

        public static PlaylistFailOn Parse(JSONObject failOn, IEnumerable<string> defaults)
        {
            var f = new PlaylistFailOn();
            if (failOn == null || failOn.type != JSONObject.Type.Array || failOn.list == null)
            {
                foreach (var d in defaults ?? Array.Empty<string>()) f.AddWord(d);
                return f;
            }
            foreach (var x in failOn.list)
            {
                if (x == null) continue;
                if (x.type == JSONObject.Type.String) { f.AddWord(x.stringValue); continue; }
                if (x.type != JSONObject.Type.Object) continue;
                var hit = x.GetField("hit");
                if (hit != null)
                {
                    string cause = hit.type == JSONObject.Type.Object ? hit.GetField("cause")?.stringValue
                                 : hit.type == JSONObject.Type.String ? hit.stringValue : null;
                    if (string.IsNullOrEmpty(cause)) f.anyHit = true; else f.hitCauses.Add(cause);
                }
                var ev = x.GetField("event");
                if (ev != null && ev.type == JSONObject.Type.String && !string.IsNullOrEmpty(ev.stringValue)) f.events.Add(ev.stringValue);
                var t = x.GetField("time");
                if (t != null && t.type == JSONObject.Type.Number && t.doubleValue > 0) f.time = t.doubleValue;
            }
            return f;
        }

        void AddWord(string w)
        {
            switch (w)
            {
                case "hazard": hazard = true; break;
                case "out": @out = true; break;
                case "fall": fall = true; break;
                case "hit": anyHit = true; break;
            }
        }

        /// Does a title event (name, its data's cause) fail the attempt? Returns the `why` ("hit" / the event name) or null.
        public string Fails(string name, string cause)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name == "hit" && (anyHit || (cause != null && hitCauses.Contains(cause)))) return "hit";
            return events.Contains(name) ? name : null;
        }

        public bool WatchesEvents => anyHit || hitCauses.Count > 0 || events.Count > 0;
    }

    /// One frame of what the wearer's device sees (all optional; null / false = nothing).
    public struct PlaylistMoveInput
    {
        public double now;
        /// Crossing: the head's distance along the start → far axis (m; 0 = the start line) and the axis length.
        public float along, length;
        /// Inside the footprint (+ a side margin), laterally.
        public bool inside;
        /// The hazard under the head (a path / name), or null.
        public string hazard;
        /// No floor under the head (a pit / the edge of the world).
        public bool noFloor;
        /// A title event on this wearer this frame (name + data.cause), or null.
        public string eventName, eventCause;
        /// Jump: the pad the head stands in (-1 = none).
        public int pad;
    }

    /// An event the rules raise: name + its data fields (the tracker emits it with the stop and the local player).
    public struct PlaylistMoveEvent
    {
        public string name, why, cause;
        public int fails, from, to;
        public override string ToString() => name + (why != null ? "(" + why + (cause != null ? " " + cause : "") + ")" : "") + (to > 0 || from > 0 ? $" {from}→{to}" : "");
    }

    public sealed class PlaylistCrossingRules
    {
        public const float EdgeBand = 0.7f, PastStart = 0.3f;
        /// Armed only at / behind the start line (after a fail the wearer must go BACK to the start; Aidan, Oct 20).
        public const float ArmAt = 0.1f;
        public enum Phase { Waiting, Armed, Crossing, Done }
        public Phase State = Phase.Waiting;
        public int Fails;
        public double StartedAt;
        public readonly PlaylistFailOn FailOn;

        public PlaylistCrossingRules(PlaylistFailOn failOn) { FailOn = failOn ?? new PlaylistFailOn(); }

        /// The start line shows while waiting / armed; the finish line while crossing.
        public bool ShowStart => State == Phase.Waiting || State == Phase.Armed;
        public bool ShowFinish => State == Phase.Crossing;

        public List<PlaylistMoveEvent> Step(PlaylistMoveInput i)
        {
            var o = new List<PlaylistMoveEvent>();
            switch (State)
            {
                case Phase.Waiting:
                case Phase.Armed:
                    // Armed standing at / behind the start line, inside the footprint's sides; the attempt starts once past it.
                    if (i.inside && i.along <= ArmAt && i.along >= -2f * EdgeBand) State = Phase.Armed;
                    else if (!i.inside || i.along < -2f * EdgeBand) State = Phase.Waiting;
                    if (State == Phase.Armed && i.inside && i.along > PastStart && i.along < i.length - EdgeBand)
                    {
                        State = Phase.Crossing; StartedAt = i.now;
                        o.Add(new PlaylistMoveEvent { name = "crossing_start", fails = Fails });
                    }
                    break;
                case Phase.Crossing:
                    if (i.inside && i.along >= i.length - EdgeBand)
                    {
                        State = Phase.Done;
                        o.Add(new PlaylistMoveEvent { name = "crossing_done", fails = Fails });
                        break;
                    }
                    string why = null, cause = null;
                    string ev = FailOn.Fails(i.eventName, i.eventCause);
                    if (ev != null) { why = ev; cause = i.eventCause; }
                    else if (FailOn.@out && !i.inside && i.along > PastStart) why = "out";
                    else if (FailOn.hazard && i.hazard != null && i.along > PastStart && i.along < i.length - EdgeBand) { why = "hazard"; cause = i.hazard; }
                    else if (FailOn.fall && i.noFloor) why = "fall";
                    else if (FailOn.time > 0 && i.now - StartedAt > FailOn.time) why = "time";
                    else if (i.along < -EdgeBand) { State = Phase.Waiting; break; }   // walked back out of the start: no fail
                    if (why != null)
                    {
                        Fails++;
                        State = Phase.Waiting;
                        o.Add(new PlaylistMoveEvent { name = "crossing_fail", why = why, cause = cause, fails = Fails });
                    }
                    break;
            }
            return o;
        }

        /// A forced reset (the goal block's stop.crossing_reset / an old stop.jump_reset): back to the start.
        public PlaylistMoveEvent? Force(string why)
        {
            if (State != Phase.Crossing) return null;
            Fails++; State = Phase.Waiting;
            return new PlaylistMoveEvent { name = "crossing_fail", why = why ?? "forced", fails = Fails };
        }
    }

    public sealed class PlaylistJumpPadRules
    {
        public const double DefaultWindow = 2.5;
        /// The last pad reached (-1 = none yet: pad 0 shows), the pad count.
        public int Reached = -1, Count;
        public bool Airborne, NeedReturn, Done;
        public double LeftAt, HazardSince = -1;
        public int Fails;
        /// A hop flies over the gap: only a head over the hazard for longer than this (a landing in it) fails.
        public const double HazardGraceS = 0.6;
        public readonly double Window;
        public readonly PlaylistFailOn FailOn;

        public PlaylistJumpPadRules(int count, double window, PlaylistFailOn failOn)
        {
            Count = Math.Max(0, count); Window = window > 0 ? window : DefaultWindow; FailOn = failOn ?? new PlaylistFailOn();
        }

        /// Pads shown: the reached pad and, while the wearer stands in it, the next one (+ the arch to it).
        public bool ShowPad(int k, int standingIn) => !Done && (k == Math.Max(0, Reached) || (k == Reached + 1 && Reached >= 0 && (standingIn == Reached || Airborne)));
        public bool ShowArchTo(int k, int standingIn) => k >= 1 && ShowPad(k, standingIn) && k == Reached + 1;

        public List<PlaylistMoveEvent> Step(PlaylistMoveInput i)
        {
            var o = new List<PlaylistMoveEvent>();
            if (Done || Count < 2) return o;
            if (Reached < 0)
            {
                if (i.pad == 0) Reached = 0;
                return o;
            }
            int next = Reached + 1;
            if (i.pad == Reached) { Airborne = false; NeedReturn = false; return o; }
            if (i.pad == next && !NeedReturn)
            {
                Airborne = false;
                o.Add(new PlaylistMoveEvent { name = "jump_hop", from = Reached, to = next, fails = Fails });
                Reached = next;
                if (Reached >= Count - 1) { Done = true; o.Add(new PlaylistMoveEvent { name = "jump_done", fails = Fails }); }
                return o;
            }
            if (i.pad >= 0 && i.pad < Reached) { Airborne = false; return o; }   // stepped back onto an earlier pad: fine
            if (NeedReturn) return o;
            if (!Airborne) { Airborne = true; LeftAt = i.now; HazardSince = -1; }
            if (i.hazard != null) { if (HazardSince < 0) HazardSince = i.now; } else HazardSince = -1;
            string why = null, cause = null;
            string ev = FailOn.Fails(i.eventName, i.eventCause);
            if (ev != null) { why = ev; cause = i.eventCause; }
            else if (FailOn.hazard && i.hazard != null && HazardSince >= 0 && i.now - HazardSince > HazardGraceS) { why = "hazard"; cause = i.hazard; }
            else if (FailOn.@out && !i.inside) why = "out";
            else if (FailOn.fall && i.noFloor) why = "fall";
            else if (i.now - LeftAt > Window) why = "window";
            if (why != null)
            {
                Fails++; Airborne = false; NeedReturn = true; HazardSince = -1;
                o.Add(new PlaylistMoveEvent { name = "jump_fail", why = why, cause = cause, fails = Fails, from = Reached });
            }
            return o;
        }
    }
}
