using System;
using UnityEngine;
using Defective.JSON;

/// <summary>
/// Minimal sample showing the DreamPark networking pattern:
/// subscribe to a <see cref="NetId"/>'s <c>OnNetEvent</c> to receive relay
/// messages, and call <see cref="DreamBoxClient.SendToNetId"/> to broadcast
/// events addressed to this object.
///
/// Attach alongside a <see cref="NetId"/> on any GameObject with a Renderer.
/// Incoming payloads with <c>{ "r": 0-1, "g": 0-1, "b": 0-1 }</c> change the
/// renderer's colour. <see cref="SendRandomColor"/> broadcasts a colour
/// change — it's wired to a UI button in the multiplayer test scene.
///
/// Note: the relay excludes the sender from <c>SendToAll</c>, so you won't
/// receive your own messages back. This sample applies the colour locally
/// when sending (optimistic update) so you see a visual change with just one
/// client connected. Additional connected clients will see the change via
/// the relay as normal.
/// </summary>
[RequireComponent(typeof(NetId))]
public class TestNetObject : MonoBehaviour
{
    private NetId _netId;
    private Renderer _renderer;
    private MaterialPropertyBlock _mpb;

    // URP/HDRP use _BaseColor; Built-in uses _Color. Setting both is harmless.
    private static readonly int PropBaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int PropColor = Shader.PropertyToID("_Color");

    void Awake()
    {
        _netId = GetComponent<NetId>();
        _renderer = GetComponent<Renderer>();
        _mpb = new MaterialPropertyBlock();
    }

    void OnEnable()
    {
        if (_netId != null) _netId.OnNetEvent += HandleNetEvent;
    }

    void OnDisable()
    {
        if (_netId != null) _netId.OnNetEvent -= HandleNetEvent;
    }

    /// <summary>
    /// Handles incoming relay messages routed to this NetId.
    /// Payload is raw JSON sent by the author of the event.
    /// </summary>
    private void HandleNetEvent(string payload)
    {
        try
        {
            var json = new JSONObject(payload);
            var payloadJson = json.GetField("payload"); 
            if (payloadJson.HasField("r") && payloadJson.HasField("g") && payloadJson.HasField("b"))
            {
                float r = (float)payloadJson.GetField("r").floatValue;
                float g = (float)payloadJson.GetField("g").floatValue;
                float b = (float)payloadJson.GetField("b").floatValue;
                SetColor(new Color(r, g, b));
            } else {
                Debug.Log($"[TestNetObject] No color fields found");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TestNetObject] couldn't parse payload: {ex.Message}\n{payload}");
        }
    }

    /// <summary>
    /// Pick a random colour and broadcast it via the relay to all other
    /// connected peers. Also applies locally so the single-client dev case
    /// still produces a visible change.
    /// </summary>
    public void SendRandomColor()
    {
        float r = UnityEngine.Random.value;
        float g = UnityEngine.Random.value;
        float b = UnityEngine.Random.value;

        // Optimistic local apply — the relay doesn't bounce sender messages back.
        SetColor(new Color(r, g, b));

        var client = DreamBoxClient.Instance;
        if (client != null && client.ConnectionState == DreamBoxClient.State.Connected)
        {
            string payload = "{\"r\":" + r.ToString("F2") + ",\"g\":" + g.ToString("F2") + ",\"b\":" + b.ToString("F2") + "}";
            client.SendToNetId(_netId.Id, "color", payload);
        }
    }

    private void SetColor(Color c)
    {
        if (_renderer == null) return;
        _renderer.GetPropertyBlock(_mpb);
        _mpb.SetColor(PropBaseColor, c);
        _mpb.SetColor(PropColor, c);
        _renderer.SetPropertyBlock(_mpb);
    }
}
