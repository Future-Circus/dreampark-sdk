namespace LanMonitor;

/// <summary>
/// Decides which session to observe, and re-decides when that session dies.
///
/// The valuable behaviour here is following a park across a host re-election.
/// When the host headset is doffed, its relay goes silent with no goodbye and
/// everyone re-elects in 1–3 s. Nothing about the new session is the same —
/// new host, new IP, new port, new per-session key — so a monitor pinned to a
/// host would go dark exactly when the interesting thing happened. Pinned to a
/// <c>parkId</c>, it just re-attaches to whoever won and the feed continues.
/// </summary>
public sealed class AttachDirector
{
    /// <summary>Give a connection attempt this long before writing it off and trying the newest beacon again.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Don't hammer a host that is rejecting or unreachable.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly MonitorState _state;
    private readonly SessionObserver _observer;

    private DateTime _attemptStartedUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private string? _lastReportedWait;

    public AttachDirector(MonitorState state, SessionObserver observer)
    {
        _state = state;
        _observer = observer;
    }

    public void Tick()
    {
        var nowUtc = DateTime.UtcNow;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ── Already observing ────────────────────────────────────────
        if (_observer.IsConnected)
        {
            // Deliberately NOT checking beacon freshness here. The connection
            // is liveness ground truth; beacons are lossy on phone hotspots and
            // congested venue Wi-Fi. dreampark-core's spec is explicit that
            // beacon-silence must never kill a connected session — the same
            // mistake would make this tool drop out mid-capture on exactly the
            // networks where you most need it.
            _attemptStartedUtc = DateTime.MinValue;
            return;
        }

        // ── Attempt in flight ────────────────────────────────────────
        if (_state.State == AttachState.Connecting)
        {
            if (_attemptStartedUtc != DateTime.MinValue && nowUtc - _attemptStartedUtc < ConnectTimeout)
                return;

            Console.Error.WriteLine($"[attach] connect to {_state.AttachedSessionId} timed out — will retry with the newest beacon.");
            _observer.Detach();
            _state.State = AttachState.Idle;
            _state.StatusMessage = "connect timed out — retrying";
            _attemptStartedUtc = DateTime.MinValue;
        }

        // ── Auto-attach ──────────────────────────────────────────────
        var pinned = _state.PinnedParkId;
        if (string.IsNullOrEmpty(pinned))
        {
            if (_state.State != AttachState.Rejected)
                _state.StatusMessage = "no parkId pinned — pick a session to observe";
            return;
        }

        if (nowUtc - _lastAttemptUtc < RetryInterval) return;

        var candidate = SelectForPark(pinned, _state.PinnedChannel, nowMs);
        if (candidate == null)
        {
            ReportWaiting(pinned, nowMs);
            return;
        }

        _lastAttemptUtc = nowUtc;
        _attemptStartedUtc = nowUtc;
        Console.WriteLine($"[attach] parkId '{pinned}' → {candidate.SessionId} " +
                          $"(hostType={candidate.HostType}, hostId={Short(candidate.HostId)})");
        _observer.Attach(candidate);
    }

    /// <summary>Panel-driven attach. Honours the pick even if the session's beacons have gone stale.</summary>
    public void AttachManually(string sessionId)
    {
        if (!_state.Sessions.TryGetValue(sessionId, out var entry))
        {
            _state.StatusMessage = $"unknown session '{sessionId}' — it may have aged out";
            return;
        }

        // A manual pick wins over the pin, otherwise the director would drag
        // you straight back to the pinned park on the next tick.
        _state.PinnedParkId = "";
        _lastAttemptUtc = DateTime.UtcNow;
        _attemptStartedUtc = DateTime.UtcNow;
        _observer.Attach(entry.Latest);
    }

    /// <summary>
    /// Attach to a host given by hand. This is the escape hatch for the case
    /// where beacons cannot be heard at all — the discovery socket lost the
    /// race for :7700, or the LAN drops UDP broadcast (common on guest and
    /// corporate Wi-Fi, and on phone hotspots). The host, port and session key
    /// are all printed by the hosting headset's own log, so this stays usable
    /// when discovery is not.
    /// </summary>
    public void AttachDirect(BeaconInfo beacon)
    {
        // Register it as a session so it appears in the panel list like any
        // other, and so a later real beacon from the same host just updates it.
        _state.RecordBeacon(beacon);
        _state.PinnedParkId = "";
        _lastAttemptUtc = DateTime.UtcNow;
        _attemptStartedUtc = DateTime.UtcNow;
        _observer.Attach(beacon);
    }

    /// <summary>
    /// Pick the best live session for a park.
    ///
    /// An exact parkId match is required. A DreamBox kiosk beacon carries no
    /// parkId at all, so a kiosk is never auto-attached — it would be a guess,
    /// and attaching to the wrong room's session while claiming to watch yours
    /// is worse than attaching to nothing. Kiosks are offered as a manual pick,
    /// and <see cref="ReportWaiting"/> says so when one is sitting there live.
    /// </summary>
    private BeaconInfo? SelectForPark(string parkId, string channel, long nowMs)
    {
        BeaconInfo? best = null;

        foreach (var entry in _state.Sessions.Values)
        {
            var beacon = entry.Latest;
            if (!_state.IsLive(entry, nowMs)) continue;
            if (!string.Equals(beacon.ParkId, parkId, StringComparison.OrdinalIgnoreCase)) continue;

            // Channel keeps SDK test sessions and production sessions apart.
            // An empty channel on the beacon means the host predates the field,
            // so it stays eligible rather than being filtered into invisibility.
            if (!string.IsNullOrEmpty(channel) &&
                !string.IsNullOrEmpty(beacon.Channel) &&
                !string.Equals(beacon.Channel, channel, StringComparison.OrdinalIgnoreCase))
                continue;

            if (best == null || beacon.ReceivedAtMs > best.ReceivedAtMs) best = beacon;
        }

        return best;
    }

    private void ReportWaiting(string parkId, long nowMs)
    {
        var liveKiosk = _state.Sessions.Values
            .Where(s => _state.IsLive(s, nowMs) && s.Latest.HostType == "dreambox")
            .Select(s => s.Latest)
            .FirstOrDefault();

        var message = liveKiosk != null
            ? $"waiting for parkId '{parkId}' — a DreamBox kiosk session is live at {liveKiosk.SessionId} " +
              "but kiosk beacons carry no parkId, so attach it by hand if that's the one you want"
            : $"waiting for parkId '{parkId}' — no live session advertising it yet";

        // Only log a change, otherwise a quiet LAN fills the Unity console.
        if (message != _lastReportedWait)
        {
            _lastReportedWait = message;
            Console.WriteLine($"[attach] {message}");
        }

        if (_state.State != AttachState.Rejected) _state.StatusMessage = message;
    }

    private static string Short(string id) => string.IsNullOrEmpty(id) ? "(none)"
        : id.Length <= 12 ? id : id[..12] + "…";
}
