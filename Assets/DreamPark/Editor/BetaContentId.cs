// ─────────────────────────────────────────────────────────────────────
//  BetaContentId.cs — deriving and validating a beta upload target.
//
//  A beta target ("Upload Beta") is a wholly separate contentId from a
//  creator's real content folder, with its own independent version
//  history server-side (see docs/contracts in dreampark-web for the
//  content-collection shape). It is NOT a second local Assets/Content
//  folder — the creator keeps working in one folder, and "Upload Beta"
//  bakes a different upload-target id into the build instead of a
//  second physical copy of the project. So this class answers "what id
//  does a beta upload target for X have" and "is this id reserved",
//  not "is this a folder on disk" — that's ContentFolders' job.
//
//  WHY THE SUFFIX CONTAINS A DASH, ON PURPOSE. ContentIdSetupPopup's
//  ContentIdRegex — the rule every real, local, folder-derived contentId
//  must pass — is letters-and-digits-only (no dashes, no punctuation).
//  Choosing a suffix that itself contains a character outside that
//  charset means a real contentId can *never* collide with a derived
//  beta id, by construction, not by convention someone has to remember
//  to enforce. If the suffix were alnum-only (e.g. "Beta"), a creator
//  naming their real game "CoinCollectorBeta" would collide with the
//  derived beta id of a game named "CoinCollector" — this sidesteps
//  that class of collision entirely rather than detecting it after
//  the fact.
//
//  Path-safety (Glass, DreamPark-iOS, Sep 2026): contentId is used as a
//  filesystem path component and a JSON dictionary key on-device, so
//  the derived id must contain no '/', no ':', no leading dot. Since
//  Suffix is a fixed literal and the prefix must already pass
//  ContentIdSetupPopup.IsValid (letters/digits only), the derived id is
//  path-safe by construction — IsValidBetaTargetId still checks it
//  explicitly rather than assuming, so a future change to either rule
//  can't silently break that guarantee.
//
//  SERVER-SIDE VALIDATION IS THE REAL GATE. Everything here is
//  client-side, advisory tooling — it stops an honest creator's SDK
//  from constructing a malformed or colliding id before a network
//  round-trip, not a malicious one from doing so. The backend must
//  independently reject any create/upload where the target id doesn't
//  match this same derivation.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR && !DREAMPARKCORE
using System;

namespace DreamPark
{
    public static class BetaContentId
    {
        /// Appended to a real contentId to name its beta upload target.
        /// Contains a dash deliberately — see file header.
        public const string Suffix = "-Beta";

        /// The beta upload target id for a given real contentId, or null
        /// if releaseContentId isn't itself a valid, non-reserved local
        /// contentId (a beta target can't be derived from Sample, the
        /// un-renamed placeholder, or an already-invalid name).
        public static string DeriveFrom(string releaseContentId)
        {
            if (!ContentIdSetupPopup.IsValid(releaseContentId)) return null;
            if (ContentFolders.IsReserved(releaseContentId)) return null;
            return releaseContentId + Suffix;
        }

        /// True when `id` is shaped like a beta target id — i.e. it could
        /// only have come from DeriveFrom, never from a real local
        /// contentId (which can't contain a dash at all).
        public static bool IsBetaTargetId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (!id.EndsWith(Suffix, StringComparison.Ordinal)) return false;
            string prefix = id.Substring(0, id.Length - Suffix.Length);
            return ContentIdSetupPopup.IsValid(prefix) && !ContentFolders.IsReserved(prefix);
        }

        /// The real contentId a beta target id was derived from, or null
        /// if `id` isn't a validly-shaped beta target id.
        public static string ReleaseContentIdOf(string betaTargetId)
        {
            if (!IsBetaTargetId(betaTargetId)) return null;
            return betaTargetId.Substring(0, betaTargetId.Length - Suffix.Length);
        }

        /// Explicit, non-assumed path-safety check for the derived id —
        /// see file header re: iOS using contentId as a filesystem path
        /// component and a JSON dictionary key. Checked on the derived id
        /// itself (not inferred from its parts) so a future change to
        /// Suffix or to ContentIdRegex can't silently produce an unsafe id.
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
