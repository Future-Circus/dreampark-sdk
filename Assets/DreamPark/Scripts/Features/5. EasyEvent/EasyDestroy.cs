using UnityEngine;

public class EasyDestroy : EasyEvent
{
    public override void OnEvent(object arg0 = null)
    {
        Destroy(gameObject);
    }
}