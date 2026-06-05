using UnityEngine;

public class EasyStart : EasyEvent
{
    public override void OnEvent(object arg0 = null)
    {
        Debug.Log($"[EasyStart] {gameObject.name} OnEvent called, listeners: {onEvent?.GetPersistentEventCount()}");
        onEvent?.Invoke(arg0);
    }
}
