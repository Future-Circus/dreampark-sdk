using UnityEngine;

public class EasyLog : EasyEvent
{
    public string message;
    public override void OnEvent(object arg0 = null)
    {
        Debug.Log("[EasyLog] " + gameObject.name + " : " + message);
        onEvent?.Invoke(null);
    }
}
