using System.Collections;
using UnityEngine;
using UnityEngine.Events;

public class EasyDelay : EasyEvent
{
    public bool randomDelay = false;
    [Range(0f, 60f)] public float delay = 0.5f;
    private float _delay = 0f;
    [ShowIf("randomDelay")][Range(0f, 60f)] public float maxDelay = 0.5f;
    public override void OnEvent(object arg0 = null)
    {
        if (randomDelay) {
            _delay = Random.Range(delay, maxDelay);
        } else {
            _delay = delay;
        }
        isEnabled = true;
    }
    public void Update()
    {
        if (!isEnabled) {
            return;
        }
        _delay -= Time.deltaTime;
        if (_delay <= 0f) {
            OnEventDisable();
            onEvent?.Invoke(null);
        }
    }
}
