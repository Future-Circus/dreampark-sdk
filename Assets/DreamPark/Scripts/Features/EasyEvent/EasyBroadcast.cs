using UnityEngine;
using System.Collections.Generic;

public class EasyBroadcast : EasyEvent
{
    [Header("Broadcast Settings")]
    [Tooltip("Channel name to broadcast on. Receivers with matching channel will be triggered.")]
    public string channel = "Default";

    [Tooltip("Log broadcast events for debugging")]
    public bool debugLog = false;

    public override void OnEvent(object arg0 = null)
    {
        base.OnEvent(arg0);

        int receiverCount = EasyReceiver.Broadcast(channel, arg0);

        if (debugLog)
            Debug.Log($"[EasyBroadcast] '{gameObject.name}' broadcast on channel '{channel}' to {receiverCount} receiver(s)");

        // Broadcast is an instant action; mark disabled after firing so inspector state reflects that.
        OnEventDisable();
    }
}
