using UnityEngine;

public static class AnimationExtensions
{
    public static float BindendoLerpIn(float t)
    {
        // Clamp between 0–1
        t = Mathf.Clamp01(t);

        // Elastic Out easing (Nintendo-like bounce)
        const float c4 = (2 * Mathf.PI) / 3f;
        if (t == 0f) return 0f;
        if (t == 1f) return 1f;

        return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
    }

    public static float BindendoLerpOut(float t)
    {
        t = Mathf.Clamp01(t);
        const float c4 = (2 * Mathf.PI) / 3f;
        if (t == 0f) return 0f;
        if (t == 1f) return 1f;
        return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
    }

    public static float BindendoSmooth(float t, float frequency = 3f, float damping = 0.6f)
    {
        t = Mathf.Clamp01(t);

        // Convert to angular frequency
        float omega = frequency * 2f * Mathf.PI;

        if (damping < 1f)
        {
            float d = Mathf.Sqrt(1f - damping * damping);
            float exp = Mathf.Exp(-damping * omega * t);
            return 1f - exp * ((damping / d) * Mathf.Sin(omega * d * t) + Mathf.Cos(omega * d * t));
        }
        else
        {
            // Critically damped (no oscillation)
            return 1f - Mathf.Exp(-omega * t) * (1f + omega * t);
        }
    }
    public static float EaseInOutSine(float t)
    {
        return -(Mathf.Cos(Mathf.PI * t) - 1f) / 2f;
    }

    public static float EaseInOutCubic(float t)
    {
        return t < 0.5f
            ? 4f * t * t * t
            : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
    }
}

