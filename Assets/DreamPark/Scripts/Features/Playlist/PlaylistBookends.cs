using System;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// PLATFORM BOOKENDS (Oct 18 2026, PLAYLISTS.md § 11, PLAYLIST-BOOKENDS.md): the Start Lobby
    /// and the Results Room. They are attractions that ship inside the app (built in code by core),
    /// not inside a title. Each one stands in its own playlist leg, and that leg plays the
    /// neighbouring title. A stop with <c>contentId "DreamParkBookends"</c> is a platform stop: it
    /// is the one exception to "one title per leg" and never resolves against a title's prefabs.
    ///
    /// Pure, so the SDK validator and compiler, the core planner and the offline harnesses share it.
    /// </summary>
    public static class PlaylistBookends
    {
        public const string ContentId = "DreamParkBookends";
        public const string StartLobby = "start-lobby";
        public const string ResultsRoom = "results-room";
        /// Placement preference: seat the stop where most players stand when its leg starts.
        public const string PreferPlayers = "players";
        /// Spawn address / resourceName of a platform attraction: <c>DreamParkBookends/Platform/{id}</c>.
        public const string AddressPrefix = ContentId + "/Platform/";

        public static readonly string[] Ids = { StartLobby, ResultsRoom };

        public static bool IsPlatformContent(string contentId)
            => !string.IsNullOrEmpty(contentId) && string.Equals(contentId.Trim(), ContentId, StringComparison.OrdinalIgnoreCase);

        /// A spawn address / resourceName that core builds itself.
        public static bool IsAddress(string resourceName)
            => !string.IsNullOrEmpty(resourceName) && resourceName.StartsWith(AddressPrefix, StringComparison.OrdinalIgnoreCase);

        /// The platform id a reference names (an id, an address, or a resourceName / slug ending in
        /// the id), or null when it is not a platform attraction.
        public static string IdOf(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            string r = reference.Trim();
            int slash = r.LastIndexOf('/');
            if (slash >= 0) r = r.Substring(slash + 1);
            if (r.StartsWith("dreamparkbookends__platform__", StringComparison.OrdinalIgnoreCase))
                r = r.Substring("dreamparkbookends__platform__".Length).Replace("__", "-");
            foreach (string id in Ids)
                if (string.Equals(r, id, StringComparison.OrdinalIgnoreCase)) return id;
            return null;
        }

        public static bool IsKnown(string reference) => IdOf(reference) != null;

        public static string AddressFor(string reference)
        {
            string id = IdOf(reference);
            return id != null ? AddressPrefix + id : null;
        }

        /// A stop is a platform stop when it names the platform content id, or (content id omitted)
        /// when its attraction is a platform address.
        public static bool IsPlatformStop(string contentId, string attraction)
            => IsPlatformContent(contentId) || (string.IsNullOrEmpty(contentId) && IsAddress(attraction));

        /// Authored footprint in metres: room-sized, about a dozen people standing comfortably in its ring
        /// (≈ 3.6 m across). The planner shrinks to <see cref="MinimumFootprint"/> when the floor is tight;
        /// at runtime the ring sizes itself to the rect it stands on.
        public static Vector2 Footprint(string reference)
        {
            switch (IdOf(reference))
            {
                case StartLobby: return new Vector2(3.6f, 3.6f);
                case ResultsRoom: return new Vector2(3.6f, 3.6f);
                default: return Vector2.zero;
            }
        }

        /// The smallest a bookend stands at (its packing minimum and its measured collider).
        public static Vector2 MinimumFootprint(string reference)
        {
            switch (IdOf(reference))
            {
                case StartLobby: return new Vector2(2.0f, 2.0f);
                case ResultsRoom: return new Vector2(2.4f, 2.4f);
                default: return Vector2.zero;
            }
        }

        public static string DisplayName(string reference)
        {
            switch (IdOf(reference))
            {
                case StartLobby: return "Start Lobby";
                case ResultsRoom: return "Results Room";
                default: return reference ?? "";
            }
        }
    }
}
