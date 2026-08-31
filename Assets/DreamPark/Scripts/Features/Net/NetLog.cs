using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Central switch for verbose multiplayer diagnostics. Toggle via the
    /// "Verbose Net Logs" checkbox on DreamBoxClient (works live in Play Mode),
    /// or set <see cref="Verbose"/> from code on device builds.
    ///
    /// Behind the switch: per-beacon discovery logs, inbound message previews,
    /// relay fan-out logs, NetId registrations. NOT behind the switch: warnings
    /// that indicate real problems (unregistered NetId, no subscribers, ignored
    /// beacons, oversized messages) — those always log, they're rare and load-
    /// bearing when debugging a report from the field.
    /// </summary>
    public static class NetLog
    {
        public static bool Verbose;

        public static void V(string message)
        {
            if (Verbose) Debug.Log(message);
        }
    }
}
