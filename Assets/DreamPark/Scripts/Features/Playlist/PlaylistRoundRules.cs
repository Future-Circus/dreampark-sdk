// ─────────────────────────────────────────────────────────────────────
//  PlaylistRoundRules.cs — the whole playlist stands at once (multi-room
//  spaces, RELEASE PUSH Oct 18; Docs/PLAYLISTS.md § 7.10).
//
//  The packer places every round's stops up front and a round change does
//  not re-pack (plan.allStanding). The current round plays; FUTURE rounds
//  stand held behind a locked force field with a "Round 3" marker (walk up
//  to one → Dreamie: "That one's for later!"); DONE rounds stay held with a
//  subtle "Cleared" mark so players focus on the current round. One-room /
//  streaming playlists never use this. Pure: offline-testable.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DreamPark
{
    public static class PlaylistRoundRules
    {
        public enum Round { Current, Future, Done }

        /// Walking within this of a locked stop's footprint (or into it) raises `stop_locked`.
        public const float NearMargin = 0.5f;
        /// Per player per stop: Dreamie says it at most once in this long.
        public const double CueCooldownSeconds = 25.0;
        /// Comfort up close: the field dims to these when the viewer is near / inside it (never blinding).
        public const float NearAlpha = 0.4f, InsideAlpha = 0.18f, ComfortDistance = 1.2f;

        /// The whole playlist stands (every round placed up front).
        public static bool WholeArc(PlaylistRunPlan plan) =>
            plan != null && plan.allStanding && plan.legs != null && plan.legs.Count > 1;

        public static Round StateOf(int stopLeg, int currentLeg) =>
            stopLeg == currentLeg ? Round.Current : stopLeg > currentLeg ? Round.Future : Round.Done;

        /// Every stop of the arc with its round and state (empty unless <see cref="WholeArc"/>).
        public static List<(PlaylistRunStop stop, int leg, Round state)> Stops(PlaylistRunPlan plan)
        {
            var list = new List<(PlaylistRunStop, int, Round)>();
            if (!WholeArc(plan)) return list;
            for (int l = 0; l < plan.legs.Count; l++)
                foreach (var s in plan.legs[l]?.stops ?? new List<PlaylistRunStop>())
                    if (s != null && !string.IsNullOrEmpty(s.occurrenceId)) list.Add((s, l, StateOf(l, plan.legIndex)));
            return list;
        }

        /// Held = every stop that is not the current round's (future: locked field; done: cleared mark).
        public static bool Held(Round state) => state != Round.Current;

        /// A CLUB (§ 6.0a, `locks: "open"`): nothing is held or fenced — every court idle and walk-up-able, only the
        /// current round's objective highlighted (the runner's own markers).
        public static bool Open(PlaylistRunPlan plan) => plan != null && plan.Locks == "open";

        /// The floating marker / lock label of a future part: "<the playlist's word> N" ("Race 3") when it authored one, else the
        /// part's own name (its name, or its first attraction) — never "Round" (§ 2.3). "" when there is nothing to name.
        public static string Marker(int leg, string roundOne = null, string partName = null) =>
            !string.IsNullOrWhiteSpace(roundOne) ? roundOne.Trim() + " " + (leg + 1) : (partName ?? "").Trim();

        public static int RoundsUntil(int stopLeg, int currentLeg) => Math.Max(0, stopLeg - currentLeg);

        /// Dreamie's line when a player walks up to a locked stop (dreamie-quest-host owns the voiced variants).
        public static string LockedLine(PlaylistRunPlan plan)
        {
            string now = plan?.Current?.name;
            return string.IsNullOrEmpty(now) ? "That one's for later!" : "That one's for later! Finish " + now + " first.";
        }

        // ── Authored locks (Oct 19, Aidan): a stop a playlist marks `lockedUntil` other same-round stops ─────────────

        /// A stop the playlist locks until other same-round stops are done.
        public static bool Gated(PlaylistRunStop s) => s != null && s.lockedUntil != null && s.lockedUntil.Count > 0;
        /// Dreamie the moment an authored lock opens: the boss "The way's open, beat the boss!", else "The way's open!
        /// On to Mole Trench!".
        public static string UnlockedSayLine(string name, bool boss) =>
            boss || string.IsNullOrEmpty(name) ? "The way's open, beat the boss!" : $"The way's open! On to {name}!";
        public static bool HasGates(PlaylistRunLeg leg) => leg?.stops != null && leg.stops.Exists(Gated);

        /// Its requirements with their live state: each lockedUntil occurrence id → (player-facing name, over?). The name
        /// is the sibling stop's (PlaylistObjectiveText.Name), else the authored lockedNeeds fallback, else the id.
        public static List<(string name, bool done)> GateNeeds(PlaylistRunLeg leg, PlaylistRunStop gated, Func<string, PlaylistStopProgress> progress)
        {
            var list = new List<(string, bool)>();
            if (!Gated(gated)) return list;
            for (int i = 0; i < gated.lockedUntil.Count; i++)
            {
                string occ = gated.lockedUntil[i];
                PlaylistRunStop sib = leg?.stops?.Find(x => x != null && x.occurrenceId == occ);
                string name = sib != null ? PlaylistObjectiveText.Name(sib)
                    : gated.lockedNeeds != null && i < gated.lockedNeeds.Count && !string.IsNullOrEmpty(gated.lockedNeeds[i]) ? gated.lockedNeeds[i] : occ;
                list.Add((name, PlaylistRunRules.StopOver(progress?.Invoke(occ))));
            }
            return list;
        }

        /// The force field's sign (record v2 § 8.6): the stop's authored `lockSign` while a need is still open, else a
        /// playlist Lua lock's sign, else null (the platform's UnlockLine). Never once every need is done ("Unlocked!").
        public static string LockSignFor(PlaylistRunStop s, IReadOnlyList<(string name, bool done)> needs, string luaSign)
        {
            bool open = needs != null && needs.Any(n => !n.done);
            if (!open && string.IsNullOrEmpty(luaSign)) return null;
            if (open && !string.IsNullOrWhiteSpace(s?.lockSign)) return s.lockSign.Trim();
            return string.IsNullOrWhiteSpace(luaSign) ? null : luaSign.Trim();
        }

        /// "A", "A and B", "A, B, and C" (Oxford comma).
        public static string NamesList(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0) return "";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return names[0] + " and " + names[1];
            return string.Join(", ", names.Take(names.Count - 1)) + ", and " + names[names.Count - 1];
        }

        /// The sign's instruction: "Finish Lava Crossing and Mole Trench to unlock" / "Just Lava Crossing left!" / "Unlocked!".
        public static string UnlockLine(IReadOnlyList<string> remaining) =>
            remaining == null || remaining.Count == 0 ? "Unlocked!"
            : remaining.Count == 1 ? "Just " + remaining[0] + " left!"
            : "Finish " + NamesList(remaining) + " to unlock";

        /// Dreamie's line walking up to it: "Finish Lava Crossing and Mole Trench first!" / "Just Lava Crossing left!".
        public static string LockedSayLine(IReadOnlyList<string> remaining) =>
            remaining == null || remaining.Count == 0 ? "It's open now, go for it!"
            : remaining.Count == 1 ? "Just " + remaining[0] + " left!"
            : "Finish " + NamesList(remaining) + " first!";

        /// The check row: "✓ Lava Crossing  |  Mole Trench" — done ones ticked in <paramref name="doneHex"/>.
        public static string RequirementsRow(IReadOnlyList<(string name, bool done)> needs, string doneHex = null, string todoHex = null)
        {
            if (needs == null || needs.Count == 0) return "";
            return string.Join("  |  ", needs.Select(n => n.done
                ? (doneHex != null ? "<color=" + doneHex + ">✓ " + n.name + "</color>" : "✓ " + n.name)
                : (todoHex != null ? "<color=" + todoHex + ">" + n.name + "</color>" : n.name)));
        }

        /// The viewer is inside the footprint (centre, yaw rotation, size x/z in metres) grown by <paramref name="margin"/>.
        public static bool Near(Vector3 centre, Quaternion rot, Vector2 size, Vector3 head, float margin = NearMargin)
        {
            Vector3 d = Inv(rot) * (head - centre);
            return Mathf.Abs(d.x) <= size.x * 0.5f + margin && Mathf.Abs(d.z) <= size.y * 0.5f + margin;
        }

        // A unit rotation's inverse (its conjugate) without the engine call: these rules run offline too.
        static Quaternion Inv(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        public static bool CueDue(double lastAt, double now) => lastAt < 0 || now - lastAt >= CueCooldownSeconds;

        /// The field's brightness for a viewer this far OUTSIDE the footprint edge (≤ 0 = inside): full at a distance,
        /// dimming smoothly as you come close, faint inside — readable, never blinding up close.
        public static float FieldAlpha(float distanceToEdge)
        {
            if (distanceToEdge <= 0f) return InsideAlpha;
            float t = Mathf.Clamp01(distanceToEdge / ComfortDistance);
            return Mathf.Lerp(NearAlpha, 1f, t * t * (3f - 2f * t));
        }

        /// Distance from the viewer to the footprint's edge (XZ; negative inside).
        public static float DistanceToEdge(Vector3 centre, Quaternion rot, Vector2 size, Vector3 head)
        {
            Vector3 d = Inv(rot) * (head - centre);
            float dx = Mathf.Abs(d.x) - size.x * 0.5f, dz = Mathf.Abs(d.z) - size.y * 0.5f;
            if (dx <= 0f && dz <= 0f) return Mathf.Max(dx, dz);
            return new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dz, 0f)).magnitude;
        }
    }
}
