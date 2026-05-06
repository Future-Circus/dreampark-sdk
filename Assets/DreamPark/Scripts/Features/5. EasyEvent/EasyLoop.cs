using UnityEngine;

public class EasyLoop : EasyEvent
{
    public override void OnEvent(object arg0 = null)
    {
        //find top level EasyEvent and call OnEvent on it
        EasyEvent topLevelEvent = gameObject.GetComponent<EasyEvent>();
        while (topLevelEvent.aboveEvent != null) {
            topLevelEvent = topLevelEvent.aboveEvent;
        }
        topLevelEvent.OnEvent(arg0);
    }
}
