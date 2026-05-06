using UnityEngine;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class EasyReceiver : EasyEvent
{
    [Header("Receiver Settings")]
    [Tooltip("Channel name to listen on. Will be triggered when an EasyBroadcast fires on this channel.")]
    public string channel = "Default";
    
    [Tooltip("If true, ignores the above EasyEvent chain and acts as a standalone entry point (always listening)")]
    public bool ignoreAboveEvent = false;
    
    [Tooltip("Log receive events for debugging")]
    public bool debugLog = false;

    // Static registry of all receivers by channel
    private static Dictionary<string, HashSet<EasyReceiver>> receivers = new Dictionary<string, HashSet<EasyReceiver>>();
    
    private bool isListening = false;

    public override void Awake()
    {
        base.Awake();
    }

    private void OnEnable()
    {
        if (ignoreAboveEvent)
            StartListening();
    }

    private void OnDisable()
    {
        StopListening();
    }

    public override void OnValidate()
    {
        #if UNITY_EDITOR
        if (!ignoreAboveEvent)
        {
            base.OnValidate();
        }
        else
        {
            RemoveSelfLink();
        }
        #endif
    }

    /// <summary>
    /// Called by the event chain above - activates the receiver to start listening
    /// </summary>
    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;
        StartListening();
        
        if (debugLog)
            Debug.Log($"[EasyReceiver] '{gameObject.name}' activated on channel '{channel}'");
    }

    public override void OnEventDisable()
    {
        base.OnEventDisable();
        StopListening();
    }

    /// <summary>
    /// Called when a broadcast is received - fires the chain below
    /// </summary>
    public void OnBroadcastReceived(object arg0 = null)
    {
        if (!isListening)
            return;
            
        if (debugLog)
            Debug.Log($"[EasyReceiver] '{gameObject.name}' received broadcast on channel '{channel}'");

        if (ignoreAboveEvent) {
            EasyEvent[] easyEvents = GetComponents<EasyEvent>();
            foreach (var easyEvent in easyEvents) {
                easyEvent.OnEventDisable();
            }
        }
        
        onEvent?.Invoke(arg0);
    }

    private void StartListening()
    {
        if (isListening || string.IsNullOrEmpty(channel))
            return;

        if (!receivers.ContainsKey(channel))
            receivers[channel] = new HashSet<EasyReceiver>();

        receivers[channel].Add(this);
        isListening = true;
        
        if (debugLog)
            Debug.Log($"[EasyReceiver] '{gameObject.name}' listening on '{channel}'");
    }

    private void StopListening()
    {
        if (!isListening)
            return;
            
        if (receivers.ContainsKey(channel))
        {
            receivers[channel].Remove(this);
            if (receivers[channel].Count == 0)
                receivers.Remove(channel);
        }
        
        isListening = false;
        
        if (debugLog)
            Debug.Log($"[EasyReceiver] '{gameObject.name}' stopped listening");
    }

    public static int Broadcast(string channel, object arg0 = null)
    {
        if (string.IsNullOrEmpty(channel) || !receivers.ContainsKey(channel))
            return 0;

        var receiversList = new List<EasyReceiver>(receivers[channel]);
        int count = 0;
        
        foreach (var receiver in receiversList)
        {
            if (receiver != null && receiver.isListening && receiver.gameObject.activeInHierarchy)
            {
                receiver.OnBroadcastReceived(arg0);
                count++;
            }
        }

        return count;
    }

    public static int GetReceiverCount(string channel)
    {
        if (string.IsNullOrEmpty(channel) || !receivers.ContainsKey(channel))
            return 0;
        return receivers[channel].Count;
    }

    public static void ClearAll()
    {
        receivers.Clear();
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(EasyReceiver))]
public class EasyReceiverEditor : EasyEventEditor
{
    public override void OnInspectorGUI()
    {   
        EasyReceiver receiver = (EasyReceiver)target;

        base.OnInspectorGUI();
        
        if (Application.isPlaying)
        {
            EditorGUILayout.Space(5);
            int count = EasyReceiver.GetReceiverCount(receiver.channel);
            EditorGUILayout.HelpBox($"Channel '{receiver.channel}' has {count} active receiver(s)", MessageType.Info);
        }
    }
}
#endif
