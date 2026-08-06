// ─────────────────────────────────────────────────────────────────────
//  ParkSimExternalContent.cs — content the park did not find by scanning
//
//  The simulator's content scan answers one question: what is in this
//  project and this scene? That is the right question for the attraction
//  a creator is building, and the wrong one for an attraction that exists
//  only inside a downloaded Addressables catalog — published content, a
//  test build, somebody else's ride. There is no prefab on disk to find,
//  so scanning will never see it.
//
//  So a host tool can HAND the simulator a prefab. A ticket is a name, a
//  kind, and a resolver that returns the prefab to instantiate. The
//  simulator asks the resolver once per generation, which is the whole
//  reason this is a delegate rather than a stored GameObject: a catalog
//  can be remounted, a version can be swapped underneath, and the ticket
//  survives all of it by re-asking instead of holding a stale reference.
//
//  TICKETS ARE PINNED. Injected content behaves exactly like content that
//  was in your scene when you pressed Play — placed in every generation,
//  never rotated out. Tapping an attraction and then pressing Regenerate
//  to see it somewhere else is the entire point; a shuffle bag that could
//  drop it would make the tap feel like it had failed.
//
//  TICKETS DO NOT SURVIVE THE DOMAIN RELOAD, and should not: a resolver is
//  a live delegate and an Addressables handle dies with the play session.
//  A host that wants a tap to outlive a play-mode cycle persists its own
//  descriptor and re-adds the ticket — which is the only place that knows
//  how to resolve the prefab again anyway.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.ParkSim
{
    public class ExternalContentTicket
    {
        /// Stable identity. Re-adding the same id replaces the ticket rather
        /// than placing the attraction twice.
        public string id;

        public string displayName;

        /// What the host believes this is. Overridden by what the resolved
        /// prefab actually carries — see ParkSimContent — because a caller
        /// guessing from a catalog key path is guessing, and the components on
        /// the prefab are not.
        public ContentKind declaredKind;

        /// Where it came from, shown in the overlay: "Content Manager",
        /// "Test Channel", …
        public string origin;

        /// Returns the prefab to instantiate, or null if it cannot right now.
        /// Called once per generation.
        public Func<GameObject> resolve;
    }

    public static class ParkSimExternalContent
    {
        private static readonly List<ExternalContentTicket> _tickets = new List<ExternalContentTicket>();

        /// Raised on any add, remove or clear, so an overlay can repaint
        /// without polling the list.
        public static event Action Changed;

        public static int Count { get { return _tickets.Count; } }

        /// <summary>
        /// A COPY, deliberately. Consumers resolve each ticket as they walk
        /// this, and a resolver is host code that can perfectly reasonably
        /// decide a ticket is dead and drop it — which would throw
        /// "Collection was modified" out of the middle of a park generation.
        /// The list is a handful of entries; the copy costs nothing.
        /// </summary>
        public static List<ExternalContentTicket> Tickets
        {
            get { return new List<ExternalContentTicket>(_tickets); }
        }

        public static bool Has(string id)
        {
            return IndexOf(id) >= 0;
        }

        /// <summary>
        /// Register content for the simulator to place. Replaces any ticket
        /// with the same id.
        /// </summary>
        public static void Add(
            string id, string displayName, ContentKind declaredKind,
            Func<GameObject> resolve, string origin = null)
        {
            if (string.IsNullOrEmpty(id) || resolve == null) {
                Debug.LogWarning("[ParkSim] Ignored external content with no id or no resolver.");
                return;
            }

            var ticket = new ExternalContentTicket {
                id = id,
                displayName = string.IsNullOrEmpty(displayName) ? id : displayName,
                declaredKind = declaredKind,
                origin = origin,
                resolve = resolve,
            };

            int existing = IndexOf(id);
            if (existing >= 0) _tickets[existing] = ticket;
            else _tickets.Add(ticket);

            Raise();
        }

        public static bool Remove(string id)
        {
            int i = IndexOf(id);
            if (i < 0) return false;
            _tickets.RemoveAt(i);
            Raise();
            return true;
        }

        public static void Clear()
        {
            if (_tickets.Count == 0) return;
            _tickets.Clear();
            Raise();
        }

        private static int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < _tickets.Count; i++) {
                if (_tickets[i] != null && _tickets[i].id == id) return i;
            }
            return -1;
        }

        private static void Raise()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
