using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Visual/audio response for the reusable Sequence controls. The trigger
    /// collider stays on this object; only its child visual is compressed.
    /// </summary>
    public sealed class DreamSequenceButtonFeedback : MonoBehaviour
    {
        public Transform visual;
        [Range(0.02f, 1f)] public float pressedHeight = 0.08f;
        [Min(0.01f)] public float pressSeconds = 0.12f;
        [Min(0f)] public float resetAfterSeconds = 2f;
        [Min(0.01f)] public float releaseSeconds = 0.18f;
        public AudioClip pressSound;
        [Range(0f, 1f)] public float soundVolume = 0.65f;

        private Vector3 restingScale;
        private Vector3 restingPosition;
        private float visualBaseY;
        private float pressedAt;
        private bool pressed;
        private static AudioClip defaultPressSound;

        private void Awake()
        {
            if (visual == null) visual = transform.Find("Press Visual");
            if (visual != null)
            {
                restingScale = visual.localScale;
                restingPosition = visual.localPosition;
                MeshFilter mesh = visual.GetComponent<MeshFilter>();
                visualBaseY = mesh != null && mesh.sharedMesh != null
                    ? mesh.sharedMesh.bounds.min.y : 0f;
            }
        }

        public bool TryPress()
        {
            if (!isActiveAndEnabled || visual == null || pressed) return false;
            // The package may have resized this level since Awake. Use its
            // current fitted pose rather than the prefab's authored scale.
            restingScale = visual.localScale;
            restingPosition = visual.localPosition;
            pressed = true;
            pressedAt = Time.unscaledTime;
            (pressSound != null ? pressSound : GetDefaultPressSound())
                .PlaySFX(transform.position, soundVolume);
            return true;
        }

        private void Update()
        {
            if (!pressed || visual == null) return;
            float elapsed = Time.unscaledTime - pressedAt;
            float height = EvaluateHeight(elapsed, pressSeconds, resetAfterSeconds,
                releaseSeconds, pressedHeight);
            if (elapsed >= resetAfterSeconds + releaseSeconds) pressed = false;
            visual.localScale = new Vector3(restingScale.x, restingScale.y * height, restingScale.z);
            visual.localPosition = restingPosition + Vector3.up *
                (visualBaseY * restingScale.y * (1f - height));
        }

        public static float EvaluateHeight(float elapsed, float pressDuration,
            float resetAfter, float releaseDuration, float pressedHeight)
        {
            if (elapsed < pressDuration)
                return Mathf.Lerp(1f, pressedHeight,
                    Mathf.Clamp01(elapsed / Mathf.Max(0.01f, pressDuration)));
            if (elapsed < resetAfter) return pressedHeight;
            return Mathf.Lerp(pressedHeight, 1f,
                Mathf.Clamp01((elapsed - resetAfter) / Mathf.Max(0.01f, releaseDuration)));
        }

        private void OnDisable()
        {
            if (visual != null)
            {
                visual.localScale = restingScale;
                visual.localPosition = restingPosition;
            }
            pressed = false;
        }

        private static AudioClip GetDefaultPressSound()
        {
            if (defaultPressSound != null) return defaultPressSound;
            const int sampleRate = 22050;
            const float duration = 0.14f;
            int count = Mathf.CeilToInt(sampleRate * duration);
            var samples = new float[count];
            float phase = 0f;
            uint noise = 1;
            for (int i = 0; i < count; i++)
            {
                float time = (float)i / sampleRate;
                phase += 2f * Mathf.PI * Mathf.Lerp(190f, 95f, time / duration) / sampleRate;
                noise = noise * 1664525u + 1013904223u;
                float click = ((noise >> 16) / 32768f - 1f) * Mathf.Exp(-time * 85f);
                float thump = Mathf.Sin(phase) * Mathf.Exp(-time * 24f);
                samples[i] = Mathf.Clamp((click * 0.28f + thump * 0.42f), -1f, 1f);
            }
            defaultPressSound = AudioClip.Create("DreamPark Button Press", count, 1, sampleRate, false);
            defaultPressSound.SetData(samples, 0);
            return defaultPressSound;
        }
    }
}
