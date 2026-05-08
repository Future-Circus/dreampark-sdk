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

    [HideInInspector] public NetId _testNetId;

    public State ConnectionState { get; private set; } = State.Disconnected;

    // -----------------------------------------------------------
    // Public read-only stats (used by RelayDebugHUD, SpectateView)
    // -----------------------------------------------------------
    public int Ping => _server?.Ping ?? -1;
    public int RTT => _server?.RoundTripTime ?? -1;
    public int MessageCount { get; private set; }
    public string ServerAddress => $"{serverIP}:{serverPort}";

    /// <summary>Fired when connection state changes. Arg is the new state.</summary>
    public event Action<State> OnConnectionStateChanged;

    /// <summary>Fired for connect/disconnect/receive/error events with a log string.</summary>
    public event Action<string> OnEventLog;

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
    }

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
        // On iOS, auto-connect to test relay for SpectateView status
        if (!connectOnStart)
        {
            connectOnStart = true;
        }
#endif

        if (connectOnStart)
        {
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
        Send(eventType, $"{{\"netId\":{netId},{payloadJson.TrimStart('{').TrimEnd('}')}}}");
    }

    // -----------------------------------------------------------
    // Unity lifecycle
    // -----------------------------------------------------------

    void Update()
    {
        _client?.PollEvents();

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
            string json = reader.GetString();
            Debug.Log($"[DreamBox] Received: {json}");
            MessageCount++;
            string truncated = json.Length > 80 ? json.Substring(0, 80) + "..." : json;
            OnEventLog?.Invoke($"RECV: {truncated}");
            HandleEvent(json);
            reader.Recycle();
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
        if (_server == null)
        {
            Debug.LogWarning("[DreamBox] Not connected, dropping event: " + eventType);
            return;
        }

        string json = $"{{\"type\":\"{eventType}\",\"payload\":{payloadJson}}}";
        var writer = new NetDataWriter();
        writer.Put(json);
        _server.Send(writer, DeliveryMethod.ReliableOrdered);
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

        // fallback: global events without a target object
        if (json.Contains("\"type\":\"score_update\""))
        {
            Debug.Log("[DreamBox] Score update received");
        }
        else if (json.Contains("\"type\":\"block_break\""))
        {
            Debug.Log("[DreamBox] Block break received");
        }
        else
        {
            Debug.Log("[DreamBox] Unknown event: " + json);
        }
    }
}
