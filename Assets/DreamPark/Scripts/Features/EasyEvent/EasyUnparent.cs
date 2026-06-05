using UnityEngine;

public class EasyUnparent : EasyEvent
{
    public override void OnEvent(object arg0 = null)
    {
        transform.SetParent(null, true);
        onEvent?.Invoke(null);
    }
}
