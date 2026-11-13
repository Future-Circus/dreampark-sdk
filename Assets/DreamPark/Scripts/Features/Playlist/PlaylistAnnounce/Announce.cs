// ─────────────────────────────────────────────────────────────────────
//  Announce.cs — dp.announce for prefabs with no Lua (PLAYLIST-PLAN § 4.2; pl-announce, Oct 21 2026).
//  Put it on the object: kind, item, holds. It announces when the object first starts and again whenever it is
//  switched back on (a game reset); switched off or destroyed = done (PlaylistAnnounceTag). Docs/PLAYLIST-EVENTS.md.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    [Serializable]
    public struct AnnounceHold
    {
        public AnnounceKind kind;
        [Min(1)] public int count;
    }

    [AddComponentMenu("DreamPark/Announce")]
    [DisallowMultipleComponent]
    public sealed class Announce : MonoBehaviour
    {
        [Tooltip("What this object is to a playlist challenge.")]
        public AnnounceKind kind = AnnounceKind.Collect;
        [Tooltip("Optional: what it is in this game ('coin', 'jubbo', 'crate').")]
        public string item = "";
        [Tooltip("What it holds and will release (a ! block holding 1 coin = Collect × 1). Counted from the start.")]
        public List<AnnounceHold> holds = new List<AnnounceHold>();

        bool _started;

        void Start() { _started = true; Say(); }
        // Not before Start: the spawner names the attraction after Awake / the first OnEnable.
        void OnEnable() { if (_started) Say(); }

        void Say()
        {
            Dictionary<string, int> h = null;
            if (holds != null)
                foreach (var x in holds)
                    if (x.count > 0) { h ??= new Dictionary<string, int>(); string k = AnnounceKinds.Name(x.kind); h[k] = (h.TryGetValue(k, out int n) ? n : 0) + x.count; }
            PlaylistAnnounce.Announce(gameObject, AnnounceKinds.Name(kind), item, h, null);
        }
    }
}
