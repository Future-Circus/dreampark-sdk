using System.Collections;
using UnityEngine;
using UnityEngine.Events;

public class EasyScale : EasyEvent
{
    public Vector3 targetScale = Vector3.one;
    public float duration = 1f;
    public bool delayNextEvent = false;
    private Coroutine scaleCoroutine;
    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;

        // Use arg0 as target scale if provided
        if (arg0 is Vector3 v)
            targetScale = v;

        if (scaleCoroutine != null) {
            StopCoroutine(scaleCoroutine);
        }

        if (duration == 0f) {
            transform.localScale = targetScale;
            onEvent?.Invoke(arg0);
            OnEventDisable();
        } else {
            scaleCoroutine = StartCoroutine(Scale());
            if (!delayNextEvent) {
                onEvent?.Invoke(arg0);
            }
        }
    }
    private IEnumerator Scale()
    {
        if (targetScale == Vector3.zero) {
            if (TryGetComponent<Rigidbody>(out var rb)) {
                rb.isKinematic = true;
            }
        }

        float startTime = Time.time;
        Vector3 startScale = transform.localScale;
        while (Time.time - startTime < duration)
        {
            float t = (Time.time - startTime) / duration;
            transform.localScale = Vector3.Lerp(startScale, targetScale, t);
            yield return null;
        }

        transform.localScale = targetScale;

        if (delayNextEvent) {
            onEvent?.Invoke(targetScale);
        }

        OnEventDisable();
        scaleCoroutine = null;
    }
}
