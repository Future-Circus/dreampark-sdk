using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.API
{
    /// <summary>
    /// Consent latch for profile and save-data writes.
    ///
    /// A guest standing in a park is not automatically a player of every game
    /// installed there. Until they physically walk into one of YOUR
    /// attractions, your content has no business writing to their profile —
    /// so awards, storage writes and DreamPoints calls made before that point
    /// are QUEUED rather than sent.
    ///
    /// The moment the player enters a <see cref="GameArea"/> belonging to a
    /// content id, that id is latched open, everything queued for it flushes
    /// in order, and every later call goes straight through. The latch is
    /// one-way for the life of the identity: leaving the attraction does not
    /// close it again, because the guest has already opted in by walking in.
    ///
    /// This is a CORRECTNESS guard, not a security boundary. It runs on the
    /// device, inside code a determined developer could patch out — its job is
    /// to stop honest mistakes (a park-wide script awarding on Start, a prop
    /// firing before the player ever reached the room) from touching guest
    /// profiles. The server-side rate limits are the actual defence.
    /// </summary>
    public static class ContentGate
    {
        /// <summary>Cap on queued actions per content id. A build that queues
        /// more than this before the player ever walks in is looping, not
        /// waiting — drop the excess rather than grow without bound.</summary>
        public const int MaxQueuedPerContent = 64;

        /// <summary>Work whose owning content id couldn't be resolved is
        /// queued here and released by the FIRST attraction the player enters.
        /// A published package is one content id, so for virtually every build
        /// this is exactly the same thing as the per-id bucket.</summary>
        const string UnscopedBucket = "";

        /// <summary>In the editor there is often no GameArea to walk into —
        /// a developer testing a single prop would otherwise see every award
        /// silently queue forever. Editor sessions therefore open the gate on
        /// first use. Set this false to exercise the real device behaviour.
        /// Has no effect in a player build.</summary>
        public static bool AutoOpenInEditor = true;

        static readonly HashSet<string> _open = new HashSet<string>();
        static readonly Dictionary<string, List<Action>> _pending = new Dictionary<string, List<Action>>();
        static bool _hooked;

        /// <summary>Fired when a content id latches open. Arg = content id.</summary>
        public static event Action<string> OnOpened;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;

            // GameArea.Enter fires this with the zone the player just walked
            // into. That physical entry IS the consent signal.
            GameArea.OnContentZoneChanged += (previous, entered) =>
            {
                if (entered != null && !string.IsNullOrEmpty(entered.gameId))
                    Open(entered.gameId);
            };

            // A new guest has not walked into anything. Drop the latch AND the
            // queue — work banked by the previous player must never land on
            // the next one.
            ProfileAPI.OnIdentityCleared += Reset;
        }

        /// <summary>True once the player has entered an attraction belonging
        /// to this content id (or any attraction at all, for unscoped work).</summary>
        public static bool IsOpen(string contentId)
        {
            if (AutoOpenInEditor && Application.isEditor) return true;

            // Standing inside an attraction RIGHT NOW is the strongest form of
            // the signal, and checking it directly makes the latch robust to a
            // missed Enter(). That matters: identity is cleared and re-bound on
            // session cycle / sleep, and if the player never left the zone
            // GameArea will not fire Enter() again (it never Exited) — without
            // this, every write after a rebind would be held forever.
            var here = GameArea.currentGameArea;
            if (here != null && !string.IsNullOrEmpty(here.gameId)
                && (string.IsNullOrEmpty(contentId) || here.gameId == contentId))
                return true;

            if (_open.Count == 0) return false;
            if (string.IsNullOrEmpty(contentId)) return true; // any entry releases unscoped work
            return _open.Contains(contentId);
        }

        /// <summary>Run <paramref name="work"/> now if the player has entered
        /// this content, otherwise hold it until they do. Pass a null or empty
        /// contentId when the calling content can't be resolved — the first
        /// attraction the player enters releases it.</summary>
        public static void Run(string contentId, Action work)
        {
            if (work == null) return;

            if (IsOpen(contentId))
            {
                work();
                return;
            }

            var key = string.IsNullOrEmpty(contentId) ? UnscopedBucket : contentId;
            if (!_pending.TryGetValue(key, out var list))
            {
                list = new List<Action>();
                _pending[key] = list;

                Debug.Log($"[ContentGate] Holding writes for '{(key == UnscopedBucket ? "<this game>" : key)}' " +
                          "until the player enters one of its attractions. This is normal — they'll be sent on entry.");
            }

            if (list.Count >= MaxQueuedPerContent)
            {
                Debug.LogWarning($"[ContentGate] Queue for '{key}' is full ({MaxQueuedPerContent}) — dropping the oldest. " +
                                 "Something is writing to the profile in a loop before the player has entered an attraction.");
                list.RemoveAt(0);
            }

            list.Add(work);
        }

        /// <summary>Latch a content id open and flush anything held for it.
        /// Called automatically on GameArea entry; exposed for tests and for
        /// content that legitimately has no GameArea.</summary>
        public static void Open(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return;
            if (!_open.Add(contentId)) return; // already open — nothing to do

            Debug.Log($"[ContentGate] '{contentId}' opened — the player entered one of its attractions.");

            Flush(contentId);
            Flush(UnscopedBucket); // the first entry also releases unscoped work

            try { OnOpened?.Invoke(contentId); }
            catch (Exception e) { Debug.LogWarning($"[ContentGate] OnOpened handler threw: {e.Message}"); }
        }

        static void Flush(string key)
        {
            if (!_pending.TryGetValue(key, out var list)) return;
            _pending.Remove(key);
            if (list.Count == 0) return;

            Debug.Log($"[ContentGate] Sending {list.Count} held write(s) for '{(key == UnscopedBucket ? "<this game>" : key)}'.");

            // Order preserved — a queued increment must not overtake the set
            // that preceded it. One bad handler must not strand the rest.
            for (int i = 0; i < list.Count; i++)
            {
                try { list[i]?.Invoke(); }
                catch (Exception e) { Debug.LogWarning($"[ContentGate] held write threw: {e.Message}"); }
            }
        }

        /// <summary>Clear every latch and discard everything queued. Called on
        /// identity change; safe to call manually between test runs.</summary>
        public static void Reset()
        {
            if (_open.Count > 0 || _pending.Count > 0)
                Debug.Log("[ContentGate] Reset — latches cleared and held writes discarded (identity changed).");

            _open.Clear();
            _pending.Clear();

            // If the player is standing in an attraction as the new identity
            // binds, they have already walked in — re-latch now rather than
            // waiting for an Enter() that will never come.
            var here = GameArea.currentGameArea;
            if (here != null && !string.IsNullOrEmpty(here.gameId)) Open(here.gameId);
        }
    }
}
