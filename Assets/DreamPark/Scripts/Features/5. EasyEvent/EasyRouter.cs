using UnityEngine;
public class EasyRouter : EasyEvent
{
    public EasyRouter[] routes;
    public bool ignoreAboveEvent = false;
    public override bool IgnoreAboveEventLink => ignoreAboveEvent;

    public override void OnValidate()
    {
        #if UNITY_EDITOR
        base.OnValidate();
        #endif
    }
    
    public override void Start () {
        eventOnStart = false;
        base.Start();
    }

    private void Disable() {
        EasyEvent[] easyEvents = GetComponents<EasyEvent>();
        foreach (var easyEvent in easyEvents) {
            easyEvent.OnEventDisable();
        }
    }

    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;
        Debug.Log(gameObject.name + " [EasyRouter] OnEvent - arg0: " + arg0);
        var disabled = false;
        if (routes.Length > 0) {
            foreach (var route in routes) {
                if (!disabled && route.gameObject == gameObject) {
                    Disable();
                    disabled = true;
                }
                route.OnEvent(arg0);
            }
        } else {
            Disable();
        }
        if (!disabled) {
            onEvent?.Invoke(arg0);
        }
    }
}
