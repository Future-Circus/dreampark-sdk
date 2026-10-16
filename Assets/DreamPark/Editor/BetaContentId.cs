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
//  SUFFIX: `Beta`, ALNUM, NO DASH — FINAL. Matches dreampark-web's
//  lib/betaTargets.js BETA_SUFFIX as of commit 0d78908. This flipped
//  three times across this room before it was settled on a
//  MEASUREMENT instead of an argument, so record the measurement, not
//  just the value: of 31 live production contentIds, 0 contain a
//  dash, 0 already end in "beta", and 2 already violate
//  ContentIdSetupPopup.ContentIdRegex outright (legacy ids that
//  predate the regex or were created outside the SDK's own gate). That
//  last number is what ends the debate — "a dash makes collision
//  impossible by construction, because ContentIdRegex forbids one"
//  assumed every real contentId satisfies ContentIdRegex, and
//  production proves some don't. So a dash suffix is not actually
//  collision-proof, only collision-resistant against ids the SDK
//  itself minted — the reservation has to be an ACTIVE check either
//  way, which it already is (IsReservedSuffix below, mirroring
//  dreampark-web's isBetaContentId). With "by construction" off the
//  table, the tie-break is which character set is already proven
//  end-to-end across every system a beta id touches — alnum is, a
//  dash would be new. Accepted cost, unchanged from the first version
//  of this file: nobody can title a game "AlphaBeta" (0 of 31 live
//  ids would have wanted to).
//
//  THE HEDGE FROM THE MIDDLE VERSION OF THIS FILE STILL APPLIES,
//  independent of the literal: never assign a beta target id into
//  ContentUploaderPanel's shared `contentId` field. That field is
//  IsValid-gated elsewhere in the file for local-folder validation,
//  and a beta target id is never a local folder. Keep it in its own
//  variable through every upload-time call site — cheap to do, and
//  it's the actual guarantee, not the suffix choice.
//
//  Path-safety (Glass, DreamPark-iOS, Sep 2026): contentId is used as a
//  filesystem path component and a JSON dictionary key on-device — an
//  alnum suffix is trivially path-safe. IsPathSafe still checks the
//  derived id explicitly rather than assuming.
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
    public enum ContentUploadTarget
    {
        Release,
        Beta,
    }

    public static class BetaContentId
    {
        /// Appended to a real contentId to name its beta upload target.
        /// Mirrors dreampark-web's lib/betaTargets.js BETA_SUFFIX — keep
        /// these in sync; see file header for the measurement behind it.
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
