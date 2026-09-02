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
//  SUFFIX: `-Beta`, WITH THE DASH — matches dreampark-web's
//  lib/betaTargets.js BETA_SUFFIX (settled at commit 7493bd6, after a
//  brief detour through an alnum `Beta`). History, because this flipped
//  twice and the reasoning matters more than the current value:
//
//  Round 1: dash, on "a beta id never round-trips through
//  ContentIdSetupPopup.ContentIdRegex, so it can't collide with a
//  hand-typed contentId — collision-proof by construction."
//
//  Round 2 (this file, briefly): alnum `Beta`, on "I haven't built the
//  real Upload Beta button yet, and ContentUploaderPanel.cs threads one
//  `contentId` field through hundreds of call sites — some IsValid-
//  gated. If a future implementation ever reuses that field for the
//  beta id instead of keeping a separate one, a dash would trip a
//  folder-name validator on an id that was never a folder." A hedge
//  against my own future carelessness, not a proven conflict.
//
//  Round 3 (final): dash, because the hedge has a cheaper fix than
//  giving up the property it was protecting. The alnum suffix has a
//  real, permanent cost — nobody can ever title a game "AlphaBeta" —
//  and the dash removes it while keeping the unsquattable guarantee,
//  PROVIDED the beta target id is never threaded through the shared
//  `contentId` field. So: use a separate field for it. When "Upload
//  Beta" is built, the target id must NOT be assigned into
//  ContentUploaderPanel's `contentId` — it needs its own variable,
//  exactly the way `idForUpload`-style locals already exist for other
//  upload-time concerns in that file. That's an engineering guarantee,
//  not a hope, and it costs nothing the design didn't already need
//  (the local folder id and the upload target id were always two
//  different things here).
//
//  CONSEQUENCE, kept from round 1: a real, hand-typed contentId cannot
//  contain a dash (ContentIdRegex), so it can never collide with a
//  derived `X-Beta` id — no active reservation lookup is needed to
//  protect a legitimately-named title. Reservation still matters for a
//  different reason: refusing `POST /add` for anything already shaped
//  like `*-Beta` so a malicious client can't mint one directly instead
//  of going through the ownership-gated allocation route. IsReservedSuffix
//  mirrors dreampark-web's isBetaContentId for that check.
//
//  Path-safety (Glass, DreamPark-iOS, Sep 2026): contentId is used as a
//  filesystem path component and a JSON dictionary key on-device — no
//  '/', no ':', no leading dot. A dash satisfies all of that; IsPathSafe
//  still checks the derived id explicitly rather than assuming.
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
        /// these in sync; see file header for why it's a dash suffix.
        public const string Suffix = "-Beta";

        /// True when `id` ends with the reserved suffix (case-insensitive,
        /// matching dreampark-web's isBetaContentId) and has a non-empty
        /// stem — a bare "-Beta" is not a beta target, it has no parent.
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
        ///
        /// CALLERS: never assign the result into ContentUploaderPanel's
        /// `contentId` field — see file header. Keep it in its own
        /// variable through every upload-time call site.
        public static string DeriveFrom(string releaseContentId)
        {
            if (!ContentIdSetupPopup.IsValid(releaseContentId)) return null;
            if (ContentFolders.IsReserved(releaseContentId)) return null;
            if (IsReservedSuffix(releaseContentId)) return null;
            return releaseContentId + Suffix;
        }

        /// True when `id` is shaped like a beta target id — i.e. it could
        /// only have come from DeriveFrom, never from a real local
        /// contentId (which can't contain a dash at all).
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
