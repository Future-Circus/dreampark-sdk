using UnityEngine;
using System.Collections;

public class EasyShake : EasyEvent
{
    [Tooltip("How intense the shake is.")]
    public float shakeIntensity = 0.05f;
    [Tooltip("How long the shake lasts (seconds).")]
    public float duration = 0.5f;
    [Tooltip("Speed of the shake noise sample.")]
    public float shakeSpeed = 100f;
    [Tooltip("Seed for the perlin noise.")]
    public float noiseSeed = 32f;
    [Tooltip("Direction multiplier for each axis.")]
    public Vector3 shakeDirection = new Vector3(0.2f, -0.2f, 1);
    [Tooltip("Return to original position afterward?")]
    public bool restoreOnFinish = true;
    public bool delayNextEvent = false;
    private Coroutine shakeRoutine;
    private Vector3 initialLocalPosition;

    public override void OnEvent(object arg0 = null)
    {
        if (shakeRoutine != null)
        {
            StopCoroutine(shakeRoutine);
            if (restoreOnFinish) transform.localPosition = initialLocalPosition;
        }
        initialLocalPosition = transform.localPosition;
        shakeRoutine = StartCoroutine(ShakeCoroutine());
        if (delayNextEvent) {
            onEvent?.Invoke(null);
        }
    }

    private IEnumerator ShakeCoroutine()
    {
        float elapsed = 0f;
        var startPos = initialLocalPosition;
        while (elapsed < duration)
        {
            float noiseX = Mathf.PerlinNoise(Time.time * shakeSpeed, noiseSeed) - 0.5f;
            float noiseY = Mathf.PerlinNoise(Time.time * shakeSpeed, noiseSeed + 1) - 0.5f;
            float noiseZ = Mathf.PerlinNoise(Time.time * shakeSpeed, noiseSeed + 2) - 0.5f;

            Vector3 shakeOffset = new Vector3(
                noiseX * shakeDirection.x,
                noiseY * shakeDirection.y,
                noiseZ * shakeDirection.z
            ) * shakeIntensity;

            transform.localPosition = startPos + shakeOffset;
            elapsed += Time.deltaTime;
            yield return null;
        }
        if (restoreOnFinish)
            transform.localPosition = startPos;

        shakeRoutine = null;
        if (delayNextEvent) {
            onEvent?.Invoke(null);
        }
    }
}
