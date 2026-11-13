// ─────────────────────────────────────────────────────────────────────
//  PlaylistObjectiveMarker.cs — the ONE-COMPONENT opt-in for playlist
//  auto-detection (Docs/PLAYLISTS.md § 13): put it on a prefab object, no Lua.
//    Collectible  each one gone (destroyed / SetActive(false)) = 1 collected
//    Boss         gone, or its EasyEnemy dies = the boss is defeated
//    Exit         the local player's head enters its collider (or 0.6 m) = reach_exit
//    Goal         gone = the stop is complete (a "level won" banner, a trophy)
//  Without markers the platform falls back to names / tags (Coin*, Boss*, Exit*).
//  DEPRECATED (PLAYLIST-PLAN § 4.2, Oct 21 2026): objects announce what they are instead — dp.announce(kind, …) or the
//  Announce component; goal blocks use stop.mark{kind} / stop.count{kind}. Kept until the titles move over.
// ─────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace DreamPark
{
    public enum PlaylistObjectiveMarkerKind { Collectible = 0, Boss = 1, Exit = 2, Goal = 3 }

    [DisallowMultipleComponent]
    public sealed class PlaylistObjectiveMarker : MonoBehaviour
    {
        public PlaylistObjectiveMarkerKind kind = PlaylistObjectiveMarkerKind.Collectible;
    }
}
