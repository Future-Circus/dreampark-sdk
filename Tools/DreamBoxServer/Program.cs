using System.Text.Json;
using DreamBoxRelay;
using LiteNetLib;
using LiteNetLib.Utils;

var configPath = RelayConfig.ResolveConfigPath(args);
var config = RelayConfig.Load(configPath);

var state = new ServerState(config);

var listener = new EventBasedNetListener();
var server = new NetManager(listener)
{
    // LiteNetLib doesn't expose a MaxConnections property on NetManager.
    // The limit is enforced in the ConnectionRequestEvent handler below.
};

server.Start(config.Port);
Console.WriteLine($"DreamBox relay running on :{config.Port} (max {config.MaxConnections} peers, debug={config.Debug})");

using var beacon = new DiscoveryBeacon(config);
beacon.Start();

using var webPanel = new WebControlPanel(state);
webPanel.Start();

// Status file writer — writes every 5 seconds so session-manager can monitor idle state
Timer? statusTimer = null;
if (!string.IsNullOrEmpty(config.StatusFilePath))
{
    Console.WriteLine($"[relay] status file: {config.StatusFilePath}");
    statusTimer = new Timer(_ =>
    {
        try
        {
            var status = JsonSerializer.Serialize(new
            {
                connectedCount = state.ConnectedCount,
                lastActivityAt = state.LastActivityAt,
            });
            // Atomic write: write to temp file then rename to avoid partial reads
            var tmp = config.StatusFilePath + ".tmp";
            File.WriteAllText(tmp, status);
            File.Move(tmp, config.StatusFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[relay] failed to write status file: {ex.Message}");
        }
    }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
}

listener.ConnectionRequestEvent += request =>
{
    if (state.ConnectedCount >= config.MaxConnections)
    {
        request.Reject();
        if (config.Debug)
            Console.WriteLine($"[relay] rejected connection — at max ({config.MaxConnections})");
        return;
    }
    request.AcceptIfKey(config.ConnectionKey);
};

listener.PeerConnectedEvent += peer =>
{
    state.Peers[peer.Id] = new PeerInfo
    {
        PeerId = peer.Id,
        Endpoint = peer.ToString() ?? "?"
    };
    state.TouchActivity();
    Console.WriteLine($"Headset connected: {peer} | Total: {server.ConnectedPeersCount}");
};

listener.PeerDisconnectedEvent += (peer, info) =>
{
    state.Peers.TryRemove(peer.Id, out _);
    state.TouchActivity();
    Console.WriteLine($"Headset disconnected: {peer} | Reason: {info.Reason}");
};

listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
{
    var bytes = reader.GetRemainingBytes();
    state.RecordRelay(bytes.Length);
    state.Log.Record(peer.Id, peer.ToString() ?? "?", bytes);

    if (config.Debug)
    {
        string message = System.Text.Encoding.UTF8.GetString(bytes);
        Console.WriteLine($"[relay] received from {peer}: {message}");
    }

    var writer = new NetDataWriter();
    writer.Put(bytes);
    server.SendToAll(writer, DeliveryMethod.ReliableOrdered, peer);
    reader.Recycle();
};

while (true)
{
    server.PollEvents();
    Thread.Sleep(config.PollIntervalMs);
}
