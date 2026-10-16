#if !DREAMPARKCORE
using System;
using System.Collections.Generic;

namespace DreamPark
{
    // Oct 13 2026 (beta-gameid-audit §4 option 1). A Beta upload stamps every
    // `gameId` field to "<id>Beta", but content also injects the title id into
    // Lua as plain string injections (gameIdFilter, storageGameId, contentId,
    // "<id>/FX_..." asset keys). Left alone, those compare the source id
    // against stamped ids on device and silently fail. ContentProcessor runs
    // every LuaBehaviour/EasyLua string injection value through Rewrite in the
    // same stamp pass, and back again in the restore pass.
    //
    // Pure on purpose: Tools/beta-injection-stamp-offline compiles this file
    // alone (no UnityEditor) and tests the rule.
    public static class BetaInjectionStamp
    {
        // A value EXACTLY equal to fromId becomes toId; a value starting with
        // fromId + "/" gets that prefix swapped. Anything else (including a
        // value that merely contains the id elsewhere) is returned unchanged.
        public static string Rewrite(string value, string fromId, string toId)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
                return value;
            if (string.Equals(fromId, toId, StringComparison.Ordinal))
                return value;
            if (string.Equals(value, fromId, StringComparison.Ordinal))
                return toId;
            if (value.Length > fromId.Length
                && value[fromId.Length] == '/'
                && value.StartsWith(fromId, StringComparison.Ordinal))
                return toId + value.Substring(fromId.Length);
            return value;
        }

        // First fromId that changes the value wins. The restore pass passes
        // both the explicit Beta target and the canonical "<id>Beta" twin so
        // an interrupted publish (or one restored by a later plain pass)
        // still flips every stamped injection back to the source id.
        public static string Rewrite(string value, IList<string> fromIds, string toId)
        {
            if (fromIds == null) return value;
            for (int i = 0; i < fromIds.Count; i++)
            {
                string next = Rewrite(value, fromIds[i], toId);
                if (!string.Equals(next, value, StringComparison.Ordinal))
                    return next;
            }
            return value;
        }

        // Which ids a pass rewrites, and into what. Stamp (source != target):
        // source -> target. Restore / plain pass (source == target): the
        // explicit Beta target if one is known, plus the canonical
        // source + betaSuffix twin, -> source.
        public static List<string> FromIdsFor(string sourceId, string targetId, string restoreFromId, string betaSuffix)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(sourceId)) return ids;
            if (string.IsNullOrEmpty(targetId)) targetId = sourceId;
            if (!string.Equals(sourceId, targetId, StringComparison.Ordinal))
            {
                ids.Add(sourceId);
                return ids;
            }
            if (!string.IsNullOrEmpty(restoreFromId)
                && !string.Equals(restoreFromId, sourceId, StringComparison.Ordinal))
                ids.Add(restoreFromId);
            if (!string.IsNullOrEmpty(betaSuffix))
            {
                string twin = sourceId + betaSuffix;
                if (!ids.Contains(twin)) ids.Add(twin);
            }
            return ids;
        }
    }
}
#endif // !DREAMPARKCORE
