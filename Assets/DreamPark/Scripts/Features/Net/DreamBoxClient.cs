using System;
using UnityEngine;
using LiteNetLib;
using LiteNetLib.Utils;
using DreamPark;
#if UNITY_EDITOR
using UnityEditor;

[CustomEditor(typeof(DreamBoxClient))]
public class DreamBoxClientEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        DreamBoxClient client = (DreamBoxClient)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
        GUI.enabled = false;
        EditorGUILayout.TextField("State", Application.isPlaying ? client.ConnectionState.ToString() : "(runtime only)");
        GUI.enabled = true;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Local Dev Server", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Skip LAN discovery and connect directly to the local dev relay " +
            "(DreamPark → Multiplayer → Start Local Server).",
            MessageType.None);
        GUI.enabled = Application.isPlaying;
        if (GUILayout.Button("Connect to Local Dev Server (127.0.0.1:7777)"))
        {
            client.Connect("127.0.0.1", 7777, "dreambox");
        }
        if (GUILayout.Button("Disconnect"))
        {
            client.Disconnect();
        }
        GUI.enabled = true;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Test Events", EditorStyles.boldLabel);

        if (GUILayout.Button("Send Score Update"))
            client.PublishScoreUpdate("player-1", UnityEngine.Random.Range(100, 999));

        if (GUILayout.Button("Send Block Break"))
            client.PublishBlockBreak(UnityEngine.Random.Range(1, 50));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Local Sim", EditorStyles.boldLabel);
        client._testNetId = (NetId)EditorGUILayout.ObjectField("Target NetId", client._testNetId, typeof(NetId), true);
        if (GUILayout.Button("Sim Color (Random)") && client._testNetId != null)
        {
            float r = UnityEngine.Random.value;
            float g = UnityEngine.Random.value;
            float b = UnityEngine.Random.value;
            string json = "{\"netId\":" + client._testNetId.Id + ",\"r\":" + r.ToString("F2") + ",\"g\":" + g.ToString("F2") + ",\"b\":" + b.ToString("F2") + "}";
            NetRegistry.Dispatch(client._testNetId.Id, json);
        }
    }
}
#endif

public class DreamBoxClient : MonoBehaviour
{
    // -----------------------------------------------------------
    // Connection state
    // -----------------------------------------------------------
    public enum State { Disconnected, Connecting, Connected, Reconnecting }

    [Header("Connection (defaults -- overridden at runtime by discovery)")]
    public string serverIP = "127.0.0.1";
    public int serverPort = 7777;
    public string connectionKey = "dreambox";

    [Header("Reconnect")]
    public bool autoReconnect = true;
    public float reconnectBaseDelay = 1f;
    public float reconnectMaxDelay = 30f;
    public int maxReconnectAttempts = 10;

    [Header("Auto-connect")]
    [Tooltip("If true, starts LAN discovery in Start() to auto-connect to the first dream-pub relay found.")]
    public bool connectOnStart = false;

    [Header("Debug")]
    [Tooltip("Verbose multiplayer diagnostics: per-beacon discovery logs, inbound message previews, relay fan-out, NetId registrations. Warnings always log regardless. Toggleable live in Play Mode.")]
    public bool verboseNetLogs = false;

    [HideInInspector] public NetId _testNetId;

    public State ConnectionState { get; private set; } = State.Disconnected;

    // -----------------------------------------------------------
    // Public read-only stats (used by RelayDebugHUD, SpectateView)
    // -----------------------------------------------------------
    public int Ping => _server?.Ping ?? -1;
    public int RTT => _server?.RoundTripTime ?? -1;
    public int MessageCount { get; private set; }

    /// <summary>Messages this client has SENT. The relay's rate limiter is
    /// per-sending-peer, so this is the number that has to stay under
    /// <see cref="EffectiveSendCap"/> — and until now content had no way to see
    /// it at all.</summary>
    public int SentCount { get; private set; }

    /// <summary>Outbound messages in the last full second.</summary>
    public int SendRate { get; private set; }

    /// <summary>
    /// The send cap that actually applies to THIS session, or 0 when there is
    /// no advertised cap.
    ///
    /// Deliberately derived rather than stored as a constant: the two relay
    /// implementations do not agree. PeerRelayServer rate-limits at
    /// MaxMessagesPerPeerPerSecond; the DreamBox kiosk relay
    /// (Tools/DreamBoxServer) has no rate limiting at all. A single hard-coded
    /// 60 would fire false "you are being dropped" warnings at every kiosk in
    /// the field — which is exactly the stale-guess failure this property
    /// exists to avoid.
    ///
    /// 0 means "unknown / uncapped", and callers must treat it as "do not
    /// warn", not as "cap of zero". The honest long-term fix is for the relay
    /// to advertise its own cap in the discovery beacon (which already carries
    /// hostType and parkId); until then this reads the arbiter's rung of the
    /// ladder, which is the only in-process signal for which relay we reached.
    /// </summary>
    public int EffectiveSendCap
    {
        get
        {
            var arbiter = DreamPark.NetSessionArbiter.Instance;
            if (arbiter == null) return 0;

            // Authoritative when the host advertises it (beacon "msgCap").
            if (arbiter.HostAdvertisedCap > 0) return arbiter.HostAdvertisedCap;

            // Fallback for a relay that does not advertise: infer from which
            // rung of the ladder we are on. Goes stale the moment a third relay
            // implementation exists, which is why the beacon field is preferred.
            switch (arbiter.State)
            {
                case DreamPark.NetSessionArbiter.SessionState.Hosting:
                case DreamPark.NetSessionArbiter.SessionState.ClientPeer:
                    return DreamPark.PeerRelayServer.MaxMessagesPerPeerPerSecond;
                default:
                    return 0;   // kiosk relay, or not in a session yet
            }
        }
    }

    /// <summary>
    /// The number a creator should design against — always the FLOOR, whoever is
    /// hosting right now.
    ///
    /// This exists because <see cref="EffectiveSendCap"/> is the wrong number to
    /// put in someone's head. It is honest about the current host (0 when the
    /// kiosk advertises no cap), and a 0 invites content to treat a kiosk session
    /// as uncapped — which is precisely the design that breaks at the worst
    /// possible moment. NetSessionArbiter has a Reelection state: a kiosk can
    /// drop mid-session and a headset takes over, and a park tuned to kiosk
    /// headroom falls over exactly when everything else is already going wrong.
    ///
    /// So: EffectiveSendCap is diagnostic and describes today's host.
    /// DesignBudget is the contract and never moves. One number that changes
    /// under you is worse than two that don't.
    ///
    /// DELIBERATELY A LITERAL, not PeerRelayServer.MaxMessagesPerPeerPerSecond.
    /// Deriving it would quietly undo the whole point. That cap is a per-device
    /// PROTECTION value, tuned to one headset's radio budget, sitting directly
    /// beside MaxPeers — whose own comment invites raising it ("Soft cap,
    /// tunable per venue — NOT a system limit … Raise it for a DreamBox-class
    /// host"). The day someone takes that invitation for the rate cap too,
    /// every shipped park's design budget would silently move, which is exactly
    /// what this number exists to prevent.
    ///
    /// This is a CONTENT CONTRACT: it must stay &lt;= the lowest cap any host
    /// will ever enforce, and changing it is a breaking change for every park
    /// that budgeted against it.
    ///
    /// The invariant is enforced by the SDK release preflight in
    /// DreamParkReleaseCommands. It guards an SDK source edit, so publishing the
    /// SDK fails before the mismatch can reach a headset in a park where nobody is
    /// reading the console. It is deliberately not a creator-content finding.
    /// </summary>
    public const int DesignBudget = 60;

    int _sendWindow;
    float _sendWindowAt;
    float _lastRateWarnAt = -99f;

    // Outbound queue. See Enqueue().
    struct Queued { public string json; public float at; }
    readonly System.Collections.Generic.List<Queued> _outbox = new System.Collections.Generic.List<Queued>();
    const int MaxQueuedMessages = 64;
    const float QueuedTtlSeconds = 5f;

    // TWO counters, because these are different bugs with opposite fixes and one
    // number cannot tell you which you have:
    //   evicted — >64 queued while disconnected. CONTENT queued too much.
    //   expired — sat 5 s undelivered. The DRAIN never got a slot. Ours.
    int _queueEvicted;
    int _queueExpired;
    int _drainedThisWindow;

    /// <summary>Messages waiting for the link to come up. Surfaced as
    /// dp.relay().queued so content can report "queued" as a fact rather than
    /// inferring it from connection state.</summary>
    public int QueuedCount => _outbox.Count;

    public string ServerAddress => $"{serverIP}:{serverPort}";

    /// <summary>Fired when connection state changes. Arg is the new state.</summary>
    public event Action<State> OnConnectionStateChanged;

    /// <summary>Fired for connect/disconnect/receive/error events with a log string.</summary>
    public event Action<string> OnEventLog;

    /// <summary>
    /// Fired for inbound events that carry no netId (session-level events, e.g.
    /// "host_leaving"). Args: (type, full JSON). Used by NetSessionArbiter.
    /// </summary>
    public event Action<string, string> OnGlobalEvent;

    private NetManager _client;
    private NetPeer _server;
    private EventBasedNetListener _listener;

    // Reconnect state
    private int _reconnectAttempts;
    private float _reconnectDelay;
    private float _reconnectTimer;
    private bool _intentionalDisconnect;

#if UNITY_IOS
    // iOS Swift bridge: periodic ping update interval
    private const float _iosPingInterval = 2f;
    private float _iosPingTimer;
#endif

    // Discovery
    private DiscoveryListener _discovery;

    /// <summary>
    /// The dreamboxId from the last discovered LAN beacon.
    /// Used by HeadsetCheckin as the locationId for temporal session pairing.
    /// </summary>
    public string DiscoveredDreamboxId { get; private set; }

    // Singleton access for convenience (optional)
    public static DreamBoxClient Instance { get; private set; }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this)
        {
            Debug.LogWarning("[DreamBox] Duplicate DreamBoxClient destroyed.");
            Destroy(this);
            return;
        }
        global::DreamPark.NetLog.Verbose = verboseNetLogs;
    }

#if UNITY_EDITOR
    // Lets the Inspector checkbox flip verbosity live during Play Mode.
    void OnValidate()
    {
        if (Application.isPlaying && Instance == this)
            global::DreamPark.NetLog.Verbose = verboseNetLogs;
    }
#endif

    void Start()
    {
        InitNetManager();
        SessionContext.OnSessionPaired += OnSessionPaired;

#if UNITY_ANDROID
        // On Quest/Android, auto-connect to test relay for diagnostics
        if (!connectOnStart)
        {
            connectOnStart = true;
        }
#endif

#if UNITY_IOS
        // DreamBox connectivity on MOBILE is an opt-in app setting (default
        // OFF) — the discovery listener spams logs and burns battery while
        // the feature is pre-release. The iOS app toggles it at runtime via
        // the DREAMBOX_DISCOVERY message (NativeInterfaceManager), which
        // also persists the PlayerPrefs flag read here.
        if (!connectOnStart && PlayerPrefs.GetInt("dreampark.dreamboxEnabled", 0) == 1)
        {
            connectOnStart = true;
        }
#endif

        if (connectOnStart)
        {
            // When a NetSessionArbiter is active it owns discovery, host
            // election, and connection — connecting to the first beacon here
            // would race it.
            if (global::DreamPark.NetSessionArbiter.ArbiterActive)
                Debug.Log("[DreamBox] NetSessionArbiter active — deferring discovery to arbiter.");
            else
                StartDiscovery();
        }
    }

    // -----------------------------------------------------------
    // Public API: Connection
    // -----------------------------------------------------------

    /// <summary>
    /// Connect to a relay at the given address. Call this from the discovery
    /// flow after getting relayHost/relayPort from the backend.
    /// </summary>
    public void Connect(string ip, int port, string key = null)
    {
        if (ConnectionState == State.Connected || ConnectionState == State.Connecting)
        {
            if (ip == serverIP && port == serverPort)
            {
                Debug.Log("[DreamBox] Already connected/connecting to this relay.");
                return;
            }
            // Disconnect from current relay first
            Disconnect();
        }

        serverIP = ip;
        serverPort = port;
        if (!string.IsNullOrEmpty(key)) connectionKey = key;

        _intentionalDisconnect = false;
        _reconnectAttempts = 0;
        _reconnectDelay = reconnectBaseDelay;

        if (_client == null || !_client.IsRunning) InitNetManager();

        SetState(State.Connecting);
        _client.Connect(serverIP, serverPort, connectionKey);
        Debug.Log($"[DreamBox] Connecting to {serverIP}:{serverPort}...");
    }

    /// <summary>
    /// Intentionally disconnect from the relay. Will not trigger reconnect.
    /// </summary>
    public void Disconnect()
    {
        _intentionalDisconnect = true;
        _server?.Disconnect();
        _server = null;
        SetState(State.Disconnected);
        Debug.Log("[DreamBox] Disconnected (intentional).");
    }

    // -----------------------------------------------------------
    // Public API: LAN Discovery
    // -----------------------------------------------------------

    /// <summary>
    /// Start listening for dream-pub UDP broadcast beacons on port 7700.
    /// On first valid beacon, connects to the discovered relay automatically.
    /// </summary>
    public void StartDiscovery()
    {
        if (_discovery != null && _discovery.IsRunning) return;

        _discovery = new DiscoveryListener();
        _discovery.OnRelayDiscovered += OnRelayDiscovered;
        _discovery.Start();
    }

    /// <summary>
    /// Stop the LAN discovery listener.
    /// </summary>
    public void StopDiscovery()
    {
        if (_discovery == null) return;
        _discovery.OnRelayDiscovered -= OnRelayDiscovered;
        _discovery.Stop();
        _discovery = null;
    }

    private void OnRelayDiscovered(DiscoveryListener.BeaconInfo info)
    {
        // Marshal to main thread — discovery callback fires on background thread
        MainThreadDispatcher.Execute(() =>
        {
            if (!string.IsNullOrEmpty(info.dreamboxId))
                DiscoveredDreamboxId = info.dreamboxId;

            if (ConnectionState == State.Connected || ConnectionState == State.Connecting)
            {
                Debug.Log("[DreamBox] Discovery: already connected/connecting, ignoring beacon.");
                return;
            }
            StopDiscovery();
            Connect(info.host, info.port, info.key);
        });
    }

    // -----------------------------------------------------------
    // Public API: Events
    // -----------------------------------------------------------

    public void PublishScoreUpdate(string playerId, int score)
    {
        Send("score_update", $"{{\"playerId\":\"{playerId}\",\"score\":{score}}}");
    }

    public void PublishBlockBreak(int blockId)
    {
        Send("block_break", $"{{\"blockId\":{blockId}}}");
    }

    public void PublishRaw(string eventType, string payloadJson)
    {
        Send(eventType, payloadJson);
    }

    /// <summary>
    /// Send an event targeting a specific networked object by its NetId.
    /// </summary>
    public void SendToNetId(uint netId, string eventType, string payloadJson)
    {
        Send(eventType, $"{{\"netId\":{netId}{JoinBody(payloadJson)}}}");
    }

    /// <summary>
    /// Splice a caller's JSON object body onto the netId field.
    ///
    /// The old one-liner was payloadJson.TrimStart('{').TrimEnd('}') with a comma
    /// always appended, which produced {"netId":N,} — invalid JSON — for the two
    /// most ordinary payloads there are: "{}" and "". The receiver's json_parse
    /// then failed and the event vanished with no diagnostic anywhere. Trimming
    /// ALL leading braces also mangled a body that legitimately started with a
    /// nested object, and leading whitespace defeated the trim entirely.
    ///
    /// Returns "" for an empty body, or ",<fields>" otherwise.
    /// </summary>
    static string JoinBody(string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return "";
        string body = payloadJson.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[body.Length - 1] == '}')
            body = body.Substring(1, body.Length - 2);
        body = body.Trim();
        if (body.Length == 0) return "";
        if (body[0] == ',') body = body.Substring(1).Trim();
        return body.Length == 0 ? "" : "," + body;
    }

    // -----------------------------------------------------------
    // Unity lifecycle
    // -----------------------------------------------------------

    void Update()
    {
        _client?.PollEvents();

        // Spend leftover budget on anything queued while we were disconnected.
        // Runs BEFORE the window rolls over, so the drain is measured against
        // this second's traffic rather than starting a fresh one.
        DrainOutbox();

        // Outbound rate meter + the warning nobody could get before.
        //
        // The relay drops message 61 of any given second on the floor: no
        // rejection, no log, no exception (PeerRelayServer.AllowRate). From
        // inside the game that is indistinguishable from a peer that stopped
        // listening. Watching our own send rate is the only way the SENDER can
        // know, and the sender is the one who can fix it.
        float nowUnscaled = Time.unscaledTime;
        if (nowUnscaled - _sendWindowAt >= 1f)
        {
            SendRate = _sendWindow;
            _sendWindow = 0;
            _drainedThisWindow = 0;
            _sendWindowAt = nowUnscaled;

            // TWO thresholds, one warning site, exactly one message.
            //
            // The cap warning alone left DesignBudget unenforced at runtime: a
            // park sending 200 msg/s on a kiosk got NO signal at all, right up
            // until a reelection put it on a peer host and it fell apart. That
            // is the precise scenario the budget exists to prevent, so it needs
            // its own threshold that fires on every host type.
            //
            // DesignBudget <= cap by invariant, so "over cap" implies "over
            // budget"; picking the more specific message keeps this to one
            // warning rather than two overlapping ones.
            int cap = EffectiveSendCap;
            if (SendRate >= DesignBudget && nowUnscaled - _lastRateWarnAt > 5f)
            {
                _lastRateWarnAt = nowUnscaled;

                if (cap > 0 && SendRate >= cap)
                {
                    // Deliberately hedged. This meter's one-second window and the
                    // relay's own Environment.TickCount window are different
                    // windows, so the client cannot assert anything WAS dropped —
                    // only that it is sending at a rate the relay is entitled to
                    // truncate. The relay logs the actual drops
                    // (PeerRelayServer.ReportDrops).
                    Debug.LogWarning($"[DreamBox] Sending {SendRate} msg/s against a relay cap of " +
                                     $"{cap}/s per peer. At or above the cap the relay may be discarding " +
                                     "messages inside its own one-second window, with no error to the " +
                                     "sender — check the host's log for '[PeerRelay] Rate limit'. " +
                                     "Rate-limit, coalesce, or send absolute values less often.");
                }
                else
                {
                    Debug.LogWarning($"[DreamBox] Sending {SendRate} msg/s against a design budget of " +
                                     $"{DesignBudget}/s. This host does not enforce it, so nothing is being " +
                                     "dropped right now — but a peer host will, and a kiosk session can hand " +
                                     "the room to one mid-play (NetSessionArbiter.Reelection). Budget for " +
                                     "the floor, not for today's host.");
                }
            }
        }

        // Handle reconnect timer
        if (ConnectionState == State.Reconnecting)
        {
            _reconnectTimer -= Time.unscaledDeltaTime;
            if (_reconnectTimer <= 0f)
            {
                AttemptReconnect();
            }
        }

#if UNITY_IOS && DREAMPARKCORE
        // Periodically send ping updates to Swift when connected
        if (ConnectionState == State.Connected)
        {
            _iosPingTimer -= Time.unscaledDeltaTime;
            if (_iosPingTimer <= 0f)
            {
                _iosPingTimer = _iosPingInterval;
                UnityToSwift.Send($"RELAY_STATUS: connected|ping={Ping}");
            }
        }
#endif
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        SessionContext.OnSessionPaired -= OnSessionPaired;
        StopDiscovery();
        _intentionalDisconnect = true;
        _client?.Stop();
    }

    /// <summary>
    /// After session pairing, set the discovery filter so only beacons from
    /// this session's DreamBox are accepted.
    /// </summary>
    private void OnSessionPaired(SessionConfig config)
    {
        if (_discovery != null && !string.IsNullOrEmpty(config.dreamboxId))
        {
            _discovery.dreamboxIdFilter = config.dreamboxId;
            Debug.Log($"[DreamBox] Discovery filter set to dreamboxId={config.dreamboxId}");
        }
    }

    // -----------------------------------------------------------
    // Internal: networking
    // -----------------------------------------------------------

    private void InitNetManager()
    {
        _listener = new EventBasedNetListener();
        _client = new NetManager(_listener);
        _client.Start();

        _listener.PeerConnectedEvent += peer =>
        {
            _server = peer;
            _reconnectAttempts = 0;
            _reconnectDelay = reconnectBaseDelay;
            StopDiscovery();
            SetState(State.Connected);
            Debug.Log($"[DreamBox] Connected to relay at {peer}");
            OnEventLog?.Invoke($"CONNECTED to {peer}");
            // Draining happens in Update, spread across the budget — see DrainOutbox.
        };

        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            _server = null;
            Debug.Log($"[DreamBox] Disconnected: {info.Reason}");
            OnEventLog?.Invoke($"DISCONNECTED: {info.Reason}");

            if (_intentionalDisconnect)
            {
                SetState(State.Disconnected);
                return;
            }

            // Start reconnect cycle
            if (autoReconnect && _reconnectAttempts < maxReconnectAttempts)
            {
                ScheduleReconnect();
            }
            else
            {
                SetState(State.Disconnected);
                if (_reconnectAttempts >= maxReconnectAttempts)
                {
                    Debug.LogWarning($"[DreamBox] Reconnect exhausted after {maxReconnectAttempts} attempts.");
                }
            }
        };

        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            try
            {
                // Cap message size — a malicious LAN peer must not be able to push
                // an unbounded string into memory or the log.
                if (reader.AvailableBytes > MaxIncomingMessageBytes)
                {
                    Debug.LogWarning($"[DreamBox] Dropping oversized message ({reader.AvailableBytes} bytes).");
                    return;
                }
                string json = reader.GetString();
                if (string.IsNullOrEmpty(json) || json.Length > MaxIncomingMessageChars) return;
                MessageCount++;
                // Verbose: inbound previews make the receive hop visible without
                // a RelayDebugHUD. First 5 in full cadence, then every 50th.
                if (MessageCount <= 5 || MessageCount % 50 == 0)
                    global::DreamPark.NetLog.V($"[DreamBox] RECV #{MessageCount}: {(json.Length > 80 ? json.Substring(0, 80) + "..." : json)}");
                // Do NOT log the full payload (logcat exposure on shared headsets) —
                // only a short truncated preview.
                string truncated = json.Length > 80 ? json.Substring(0, 80) + "..." : json;
                OnEventLog?.Invoke($"RECV: {truncated}");
                HandleEvent(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamBox] Failed to handle inbound message: {e.Message}");
            }
            finally
            {
                reader.Recycle();
            }
        };

        _listener.NetworkErrorEvent += (endpoint, error) =>
        {
            Debug.LogError($"[DreamBox] Network error: {error}");
            OnEventLog?.Invoke($"ERROR: {error}");
        };
    }

    // -----------------------------------------------------------
    // Internal: reconnect
    // -----------------------------------------------------------

    private void ScheduleReconnect()
    {
        _reconnectAttempts++;
        // Exponential backoff with jitter
        _reconnectDelay = Mathf.Min(
            _reconnectDelay * 2f,
            reconnectMaxDelay
        );
        float jitter = UnityEngine.Random.Range(0f, _reconnectDelay * 0.3f);
        _reconnectTimer = _reconnectDelay + jitter;

        SetState(State.Reconnecting);
        Debug.Log($"[DreamBox] Reconnecting in {_reconnectTimer:F1}s (attempt {_reconnectAttempts}/{maxReconnectAttempts})...");
    }

    private void AttemptReconnect()
    {
        if (_intentionalDisconnect || ConnectionState != State.Reconnecting)
            return;

        SetState(State.Connecting);
        Debug.Log($"[DreamBox] Reconnect attempt {_reconnectAttempts}/{maxReconnectAttempts} to {serverIP}:{serverPort}...");

        try
        {
            if (_client == null || !_client.IsRunning) InitNetManager();
            _client.Connect(serverIP, serverPort, connectionKey);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DreamBox] Reconnect failed: {ex.Message}");
            if (_reconnectAttempts < maxReconnectAttempts)
            {
                ScheduleReconnect();
            }
            else
            {
                SetState(State.Disconnected);
                Debug.LogWarning($"[DreamBox] Reconnect exhausted after {maxReconnectAttempts} attempts.");
            }
        }
    }

    private void SetState(State newState)
    {
        if (ConnectionState == newState) return;
        ConnectionState = newState;
        OnConnectionStateChanged?.Invoke(newState);

#if UNITY_IOS && DREAMPARKCORE
        // Send relay status to Swift for SpectateView indicator
        switch (newState)
        {
            case State.Connected:
                _iosPingTimer = 0f; // trigger immediate ping update
                UnityToSwift.Send($"RELAY_STATUS: connected|ping={Ping}");
                break;
            case State.Disconnected:
                UnityToSwift.Send("RELAY_STATUS: disconnected");
                break;
            case State.Connecting:
                UnityToSwift.Send("RELAY_STATUS: connecting");
                break;
            case State.Reconnecting:
                UnityToSwift.Send("RELAY_STATUS: reconnecting");
                break;
        }
#endif
    }

    // -----------------------------------------------------------
    // Internal: message handling
    // -----------------------------------------------------------

    void Send(string eventType, string payloadJson)
    {
        string json = $"{{\"type\":\"{eventType}\",\"payload\":{payloadJson}}}";

        if (_server == null)
        {
            Enqueue(eventType, json);
            return;
        }

        Transmit(json);
    }

    void Transmit(string json)
    {
        var writer = new NetDataWriter();
        writer.Put(json);
        _server.Send(writer, DeliveryMethod.ReliableOrdered);
        SentCount++;
        _sendWindow++;
    }

    /// <summary>
    /// Hold a message that was sent before the link came up, and deliver it when
    /// it does.
    ///
    /// Discovery takes a second or two, so EVERY park's join handshake — the
    /// "hello, who is here" that decides join order, colour and team — was being
    /// sent into a null peer and logged away. Content could not reasonably be
    /// expected to solve that individually, and each park that tried solved it
    /// differently.
    ///
    /// This is a handshake safety net, NOT a reliability layer, and it is bounded
    /// on both axes so it cannot become one:
    ///   • 64 messages, oldest dropped first — a disconnected pose stream cannot
    ///     accumulate, and a reconnect cannot fire a flood at the relay
    ///   • 5 second TTL — nothing stale enough to be wrong gets delivered
    ///   • every drop is counted and logged, so a park leaning on it is visible
    ///
    /// Content that needs to decide FOR ITSELF what to re-send on connect should
    /// use dp.on_connected() instead of relying on this.
    /// </summary>
    void Enqueue(string eventType, string json)
    {
        PruneOutbox();

        if (_outbox.Count >= MaxQueuedMessages)
        {
            _outbox.RemoveAt(0);
            _queueEvicted++;
            if (_queueEvicted == 1 || _queueEvicted % 32 == 0)
                Debug.LogWarning($"[DreamBox] Outbound queue full ({MaxQueuedMessages}) while disconnected — " +
                                 $"evicted {_queueEvicted} message(s), oldest first. The queue is a join-handshake " +
                                 "safety net, not a reliability layer: stop sending while disconnected, or use " +
                                 "dp.on_connected() to re-send deliberately.");
        }

        _outbox.Add(new Queued { json = json, at = Time.unscaledTime });
        global::DreamPark.NetLog.V($"[DreamBox] Queued '{eventType}' until the link is up ({_outbox.Count} waiting).");
    }

    void PruneOutbox()
    {
        float now = Time.unscaledTime;
        int expired = 0;
        for (int i = _outbox.Count - 1; i >= 0; i--)
            if (now - _outbox[i].at > QueuedTtlSeconds)
            {
                _outbox.RemoveAt(i);
                expired++;
            }
        if (expired == 0) return;

        _queueExpired += expired;
        // This one is the SDK's fault, not content's, and it says so. A message
        // that ages out while CONNECTED means the drain never got a slot — live
        // traffic ate the whole budget — and the messages lost are the oldest,
        // which is to say the join handshake.
        Debug.LogWarning($"[DreamBox] {expired} queued message(s) expired undelivered after " +
                         $"{QueuedTtlSeconds:0}s ({_queueExpired} total). " +
                         (_server != null
                            ? "The outbox is not draining — live traffic is consuming the send budget."
                            : "Still disconnected; nothing could be sent."));
    }

    /// <summary>
    /// Drain the outbox WITHOUT overrunning the send budget.
    ///
    /// The first version flushed all 64 in the frame the link came up. Against a
    /// 60/s per-peer cap that overruns by construction — and the messages the
    /// relay truncates are the join handshake, which is the exact thing the
    /// queue was built to protect. It fixed the bug at the delivery layer and
    /// reintroduced it one hop later.
    ///
    /// So the drain spends only what is left of this second's budget, minus
    /// headroom for live traffic, and continues on following frames. A full
    /// 64-message backlog takes about a second and a half to clear and never
    /// pushes the second's total over the cap.
    /// </summary>
    void DrainOutbox()
    {
        if (_outbox.Count == 0 || _server == null) return;
        PruneOutbox();
        if (_outbox.Count == 0) return;

        // METER AGAINST DesignBudget, NEVER EffectiveSendCap.
        //
        // EffectiveSendCap is 0 on a kiosk session by design ("no advertised
        // cap"), and 0 here would make allowance <= 0 every frame, forever: the
        // queue built to rescue the join handshake would be inert on the host
        // type most parks actually run, and the symptom would be the ORIGINAL
        // bug — hellos vanishing — with a queue in place that looks like it is
        // handling them. DesignBudget is never 0, never moves, and is by
        // construction <= whatever the real host enforces, so this is safe on a
        // peer host and merely conservative on a kiosk.
        int headroom = Mathf.Max(1, DesignBudget / 4);
        int spare = (DesignBudget - headroom) - _sendWindow;

        // RESERVED FLOOR — without it the drain is starvable, and starving it
        // recreates the exact bug the queue exists to prevent.
        //
        // `spare` is what live traffic left over. In a scene whose live traffic
        // already meets the budget on the first frame — ten attractions in one
        // test scene, say — spare is <= 0 on every frame forever. The backlog
        // then sits until the 5 s TTL expires it, and the messages lost are the
        // oldest ones: the hellos. The queue would be reporting drops while
        // looking like it was handling them.
        //
        // So the outbox gets a share live traffic cannot consume. DesignBudget/4
        // is 15/s, which clears a full 64-message backlog in ~4.3 s — inside the
        // TTL, by design. It can overshoot the budget by a quarter while a
        // backlog exists, and that is the intended trade: a queued handshake
        // matters more than a pose update, and the overshoot is bounded and
        // ends when the backlog does.
        int reserved = Mathf.Max(1, DesignBudget / 4);
        int allowance = Mathf.Max(reserved - _drainedThisWindow, spare);
        if (allowance <= 0) return;

        int n = Mathf.Min(allowance, _outbox.Count);
        for (int i = 0; i < n; i++) Transmit(_outbox[i].json);
        _outbox.RemoveRange(0, n);
        _drainedThisWindow += n;

        if (_outbox.Count == 0)
            Debug.Log($"[DreamBox] Link up — drained {n} queued message(s).");
        else
            global::DreamPark.NetLog.V($"[DreamBox] Drained {n}, {_outbox.Count} still queued.");
    }

    // Hard caps on inbound LAN messages (untrusted peers). 16 KB is generous for
    // the small JSON events this protocol uses.
    const int MaxIncomingMessageBytes = 16 * 1024;
    const int MaxIncomingMessageChars = 16 * 1024;
    const int MaxLogPreviewChars = 512;

    /// <summary>
    /// Cheap extraction of the "type" field from the wire JSON
    /// {"type":"...","payload":{...}} — same allocation-light style as the
    /// netId scan above. Returns null if absent/malformed.
    /// </summary>
    static string ExtractTypeField(string json)
    {
        int idx = json.IndexOf("\"type\":\"", StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = idx + 8;
        int end = json.IndexOf('"', start);
        if (end <= start || end - start > 64) return null;
        return json.Substring(start, end - start);
    }

    static string TruncateForLog(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxLogPreviewChars)
            return value;

        return value.Substring(0, MaxLogPreviewChars) + $"... ({value.Length - MaxLogPreviewChars} chars truncated)";
    }

    void HandleEvent(string json)
    {
        // try to route to a specific NetId
        // expected wire format: {"type":"...", "netId": 12345, ...}
        uint netId = 0;
        bool hasNetId = false;

        int idx = json.IndexOf("\"netId\":");
        if (idx >= 0)
        {
            int start = idx + 8;
            while (start < json.Length && json[start] == ' ') start++;
            int end = start;
            while (end < json.Length && char.IsDigit(json[end])) end++;
            if (end > start && uint.TryParse(json.Substring(start, end - start), out netId))
                hasNetId = true;
        }

        if (hasNetId)
        {
            NetRegistry.Dispatch(netId, json);
            return;
        }

        // Session-level events (no netId): surface the type to subscribers
        // (NetSessionArbiter listens for "host_leaving").
        if (OnGlobalEvent != null)
        {
            string type = ExtractTypeField(json);
            if (type != null)
            {
                try { OnGlobalEvent.Invoke(type, json); }
                catch (Exception e) { Debug.LogWarning($"[DreamBox] OnGlobalEvent handler threw: {e.Message}"); }
            }
        }

        // fallback: global events without a target object
        if (json.Contains("\"type\":\"score_update\""))
        {
            Debug.Log("[DreamBox] Score update received");
        }
        else if (json.Contains("\"type\":\"pp\"") || json.Contains("\"type\":\"pp_bye\""))
        {
            // Player presence (PlayerPresence.cs) — consumed through OnGlobalEvent above,
            // at pose rate, so it must not land in the log.
        }
        else if (json.Contains("\"type\":\"block_break\""))
        {
            Debug.Log("[DreamBox] Block break received");
        }
        else
        {
            Debug.Log("[DreamBox] Unknown event: " + TruncateForLog(json));
        }
    }
}
