// ─────────────────────────────────────────────────────────────────────
//  BetaContentId.cs — deriving and validating a beta upload target.
//
//  A beta target ("Upload Beta") is a wholly separate contentId from a
//  creator's real content folder, with its own independent version
//  history server-side (see lib/betaTargets.js in dreampark-web for the
//  content-collection shape). It is NOT a second local Assets/Content
//  folder — the creator keeps working in one folder, and "Upload Beta"
//  bakes a different upload-target id into the build instead of a
//  second physical copy of the project. So this class answers "what id
//  does a beta upload target for X have" and "is this id reserved",
//  not "is this a folder on disk" — that's ContentFolders' job.
//
//  SUFFIX: `Beta`, ALNUM, NO DASH — this is dreampark-web's constant
//  (lib/betaTargets.js BETA_SUFFIX), mirrored here so all three repos
//  read one source instead of three string literals that happen to
//  agree today. An earlier version of this file used `-Beta` on the
//  reasoning that a beta target id never round-trips through
//  ContentIdSetupPopup.ContentIdRegex, so it wouldn't need to be
//  alnum-only. That reasoning was true for the code that existed at the
//  time (this file only checks the *release* id against ContentIdRegex,
//  never the derived beta id) but isn't provably true for the rest of
//  ContentUploaderPanel.cs, which threads a single `contentId` field
//  through hundreds of call sites — some of which gate on
//  ContentIdSetupPopup.IsValid for local-folder validation. If a future
//  "Upload Beta" implementation ever reuses that field for convenience,
//  a dash would trip a validator meant for folder names, on an id that
//  isn't one. Web's literal survives that risk by construction: it's
//  the intersection of every validator on any side of the wire
//  (dreampark-web's own file documents this reasoning), so nothing has
//  to be relaxed anywhere to adopt it, including here.
//
//  CONSEQUENCE: an alnum suffix means a real contentId CAN legitimately
//  collide with a derived beta id (a creator naming their own game
//  "CoinCollectorBeta"). Unlike a dash-based suffix this is NOT
//  disjoint from the real-contentId charset by construction, so it
//  needs an active reservation rule, not just a naming convention.
//  dreampark-web enforces this at creation with a flat rule — any
//  contentId ending in "beta" (case-insensitive) is refused by the
//  generic POST /api/content/add path (lib/betaTargets.js
//  isReservedBetaContentId) — accepting the known cost that nobody can
//  ever name a title "AlphaBeta". IsReservedSuffix here mirrors that
//  same flat rule so the SDK can give the same answer before a
//  creator ever reaches the network round-trip; it is advisory only,
//  the server is the real gate.
//
//  Path-safety (Glass, DreamPark-iOS, Sep 2026): contentId is used as a
//  filesystem path component and a JSON dictionary key on-device — an
//  alnum suffix is trivially path-safe, so this is now a formality
//  rather than a live constraint, but IsPathSafe still checks it
//  explicitly rather than assuming.
//
//  SERVER-SIDE VALIDATION IS THE REAL GATE. Everything here is
//  client-side, advisory tooling — it stops an honest creator's SDK
//  from constructing a malformed or reserved id before a network
//  round-trip, not a malicious one from doing so. The backend
//  independently enforces the same rule.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR && !DREAMPARKCORE
using System;

namespace DreamPark
{
    public static class BetaContentId
    {
        /// Appended to a real contentId to name its beta upload target.
        /// Mirrors dreampark-web's lib/betaTargets.js BETA_SUFFIX — keep
        /// these in sync; see file header for why it's alnum, not a dash.
        public const string Suffix = "Beta";

        /// True when `id` ends with the reserved suffix (case-insensitive,
        /// matching dreampark-web's isBetaContentId) and has a non-empty
        /// stem — a bare "Beta" is not a beta target, it has no parent.
        public static bool IsReservedSuffix(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string trimmed = id.Trim();
            if (trimmed.Length <= Suffix.Length) return false;
            return trimmed.Substring(trimmed.Length - Suffix.Length)
                .Equals(Suffix, StringComparison.OrdinalIgnoreCase);
        }

        /// The beta upload target id for a given real contentId, or null
        /// if releaseContentId isn't itself a valid, non-reserved local
        /// contentId (a beta target can't be derived from Sample, the
        /// un-renamed placeholder, an already-invalid name, or a name
        /// that is itself already shaped like a beta target).
        public static string DeriveFrom(string releaseContentId)
        {
            if (!ContentIdSetupPopup.IsValid(releaseContentId)) return null;
            if (ContentFolders.IsReserved(releaseContentId)) return null;
            if (IsReservedSuffix(releaseContentId)) return null;
            return releaseContentId + Suffix;
        }

        /// True when `id` is shaped like a beta target id.
        public static bool IsBetaTargetId(string id)
        {
            return IsReservedSuffix(id);
        }

        /// The real contentId a beta target id was derived from, or null
        /// if `id` isn't a validly-shaped beta target id. Display /
        /// messaging only — never an authorization input. The link that
        /// matters is the server-stored betaOf field, written by the
        /// allocation endpoint after it checks ownership; trusting a
        /// trimmed string for access control would make a naming
        /// convention load-bearing (dreampark-web's own file makes the
        /// same call for parentContentIdFor).
        public static string ReleaseContentIdOf(string betaTargetId)
        {
            if (!IsBetaTargetId(betaTargetId)) return null;
            return betaTargetId.Substring(0, betaTargetId.Length - Suffix.Length);
        }

        /// Explicit, non-assumed path-safety check for the derived id —
        /// see file header re: iOS using contentId as a filesystem path
        /// component and a JSON dictionary key. Checked on the derived id
        /// itself (not inferred from its parts) so a future change to
        /// Suffix can't silently produce an unsafe id.
        public static bool IsPathSafe(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id.StartsWith(".", StringComparison.Ordinal)) return false;
            foreach (char c in id)
            {
                if (c == '/' || c == '\\' || c == ':') return false;
            }
            return true;
        }
    }
}
#endif
