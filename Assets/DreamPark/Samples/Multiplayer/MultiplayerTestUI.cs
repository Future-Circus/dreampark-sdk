using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI controller for the multiplayer test scene. Wires three buttons to
/// <see cref="DreamBoxClient"/> and <see cref="TestNetObject"/>, and shows
/// the live connection state on a label.
///
/// All fields are assigned by the scene generator at build time; if you
/// hand-edit the scene, just drag the references in the Inspector.
/// </summary>
public class MultiplayerTestUI : MonoBehaviour
{
    [Header("References")]
    public DreamBoxClient client;
    public TestNetObject testProp;

    [Header("UI")]
    public Text stateLabel;
    public Button connectButton;
    public Button disconnectButton;
    public Button sendButton;

    // What the local dev server listens on — matches dev.example.json.
    private const string LocalHost = "127.0.0.1";
    private const int LocalPort = 7777;
    private const string ConnectionKey = "dreambox";

    void Start()
    {
        if (client == null) client = DreamBoxClient.Instance;

        if (connectButton != null)
            connectButton.onClick.AddListener(ConnectLocal);
        if (disconnectButton != null)
            disconnectButton.onClick.AddListener(DisconnectNow);
        if (sendButton != null)
            sendButton.onClick.AddListener(SendRandomColor);

        if (client != null)
            client.OnConnectionStateChanged += OnStateChanged;

        RefreshState();
    }

    void OnDestroy()
    {
        if (client != null)
            client.OnConnectionStateChanged -= OnStateChanged;
    }

    void Update()
    {
        // Cheap: refresh button interactability each frame so the UI reflects
        // the connection state even if we missed a state-changed event.
        RefreshButtons();
    }

    private void ConnectLocal()
    {
        if (client == null) return;
        client.Connect(LocalHost, LocalPort, ConnectionKey);
    }

    private void DisconnectNow()
    {
        if (client != null) client.Disconnect();
    }

    private void SendRandomColor()
    {
        if (testProp != null) testProp.SendRandomColor();
    }

    private void OnStateChanged(DreamBoxClient.State _)
    {
        RefreshState();
    }

    private void RefreshState()
    {
        if (stateLabel == null) return;
        stateLabel.text = client != null
            ? $"State: {client.ConnectionState}"
            : "State: (no client)";
    }

    private void RefreshButtons()
    {
        if (client == null) return;

        var state = client.ConnectionState;
        bool connected = state == DreamBoxClient.State.Connected;
        bool busy = state == DreamBoxClient.State.Connecting
                 || state == DreamBoxClient.State.Reconnecting;

        if (connectButton != null) connectButton.interactable = !connected && !busy;
        if (disconnectButton != null) disconnectButton.interactable = connected || busy;
        // Send always enabled — SendRandomColor falls back to a local-only
        // preview when disconnected, which is still useful for dev.
        if (sendButton != null) sendButton.interactable = testProp != null;

        if (stateLabel != null)
        {
            var current = client != null ? $"State: {state}" : "State: (no client)";
            if (stateLabel.text != current) stateLabel.text = current;
        }
    }
}
