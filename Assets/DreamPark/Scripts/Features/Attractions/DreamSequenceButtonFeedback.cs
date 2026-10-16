using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Visual/audio response for the reusable Sequence controls. The trigger
    /// collider stays on this object; only its child visual is compressed.
    ///
    /// Also the DELIBERATE-PRESS gate behind <c>dp.button_press()</c> (Oct 5 2026,
    /// playtest: "the start area auto-advances without the player doing anything").
    /// The shared control script fires on <c>ontriggerenter</c> + <c>dp.is_player</c>,
    /// which is true for EVERY collider under the rig — body capsule, feet kicker,
    /// head sphere — and the default START control sits on the floor at the centre
    /// of the Start Level, in a Sequence room picked nearest the player. So the
    /// guest's own body was standing in the button the moment the level came up
    /// (or was unparked, or the rig's colliders came back at the diorama landing),
    /// and the run started on its own. A press now needs:
    ///  1. ARMED: the control has been live for <see cref="armDelaySeconds"/> with
    ///     no presser inside it. Anything already overlapping when it comes up
    ///     (spawn, level swap back to Start, optimizer unpark) must leave first.
    ///     After a press it disarms the same way, so a hand resting in it cannot
    ///     re-press when the cooldown ends.
    ///  2. A PRESSER inside it right now: any collider of the LOCAL player rig
    ///     (remote avatars never press). START is stepped on by default (Aidan,
    ///     Oct 5 2026) — the auto-start was never "feet count", it was "already
    ///     inside when it came up", which (1) fixes. A control can require a
    ///     deliberate hand press with <see cref="pressedBy"/> = Hands.
    /// The check is an overlap query against this object's own trigger, so it does
    /// not depend on which collider Unity's trigger event happened to report, and
    /// shipped content using the shared script gets it without a re-upload.
    /// </summary>
    public sealed class DreamSequenceButtonFeedback : MonoBehaviour
    {
        public enum Pressers { Hands = 0, AnyPlayerCollider = 1 }

        public Transform visual;
        [Range(0.02f, 1f)] public float pressedHeight = 0.08f;
        [Min(0.01f)] public float pressSeconds = 0.12f;
        [Min(0f)] public float resetAfterSeconds = 2f;
        [Min(0.01f)] public float releaseSeconds = 0.18f;
        public AudioClip pressSound;
        [Range(0f, 1f)] public float soundVolume = 0.65f;
        [Tooltip("Who can press it. AnyPlayerCollider (default): stepping on it, or any local " +
                 "player collider. Hands: a deliberate hand press only (body/feet only count for a " +
                 "rig with no hand colliders).")]
        public Pressers pressedBy = Pressers.AnyPlayerCollider;
        [Tooltip("Seconds the control must be live, with nobody inside it, before it accepts a press.")]
        [Min(0f)] public float armDelaySeconds = 0.75f;

        private const float ClearCheckInterval = 0.1f;
        private static readonly Collider[] OverlapBuffer = new Collider[64];

        private Vector3 restingScale;
        private Vector3 restingPosition;
        private float visualBaseY;
        private float pressedAt;
        private bool pressed;
        private bool armed;
        private float liveSince;
        private float nextClearCheckAt;
        private static AudioClip defaultPressSound;

        /// <summary>Whether a press would currently be accepted if a presser were inside.</summary>
        public bool IsArmed => armed;

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
            if (!armed)
            {
                Debug.Log($"[DreamSequenceButton] {name}: press ignored — not armed yet " +
                          "(just came up, or something was already inside it; it arms once clear).", this);
                return false;
            }
            if (!HasPresserInside())
            {
                Debug.Log($"[DreamSequenceButton] {name}: press ignored — no " +
                          (pressedBy == Pressers.Hands ? "hand" : "player collider") + " inside it.", this);
                return false;
            }
            // The package may have resized this level since Awake. Use its
            // current fitted pose rather than the prefab's authored scale.
            restingScale = visual.localScale;
            restingPosition = visual.localPosition;
            pressed = true;
            armed = false;   // re-arms once the presser has left (Update)
            liveSince = Time.unscaledTime;
            pressedAt = Time.unscaledTime;
            (pressSound != null ? pressSound : GetDefaultPressSound())
                .PlaySFX(transform.position, soundVolume);
            return true;
        }

        private void OnEnable()
        {
            // Every time the control comes up (level shown, optimizer unpark) it
            // starts disarmed: whoever is already standing in it must leave first.
            armed = false;
            liveSince = Time.unscaledTime;
            nextClearCheckAt = 0f;
        }

        private void Update()
        {
            UpdateArming();
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
            armed = false;
        }

        // ── Deliberate-press gate ──────────────────────────────────────────

        private void UpdateArming()
        {
            if (armed) return;
            float now = Time.unscaledTime;
            if (now - liveSince < armDelaySeconds || now < nextClearCheckAt) return;
            nextClearCheckAt = now + ClearCheckInterval;
            // Any LOCAL player collider inside holds it disarmed — not only a
            // presser: a guest standing on a floor pad has to step off it.
            if (CountInside(requirePresser: false) == 0) armed = true;
        }

        private bool HasPresserInside() => CountInside(requirePresser: true) > 0;

        private int CountInside(bool requirePresser)
        {
            int hits = 0;
            foreach (Collider trigger in GetComponents<Collider>())
            {
                if (trigger == null || !trigger.enabled) continue;
                int n = Overlap(trigger);
                for (int i = 0; i < n; i++)
                {
                    Collider other = OverlapBuffer[i];
                    OverlapBuffer[i] = null;
                    if (other == null || other == trigger || !other.enabled) continue;
                    if (!IsLocalPlayerCollider(other)) continue;
                    if (requirePresser && !CanPress(other)) continue;
                    hits++;
                }
            }
            return hits;
        }

        private static int Overlap(Collider trigger)
        {
            const QueryTriggerInteraction q = QueryTriggerInteraction.Collide;
            if (trigger is BoxCollider box)
            {
                Transform t = box.transform;
                Vector3 s = t.lossyScale;
                Vector3 half = new Vector3(Mathf.Abs(box.size.x * s.x), Mathf.Abs(box.size.y * s.y),
                    Mathf.Abs(box.size.z * s.z)) * 0.5f;
                return Physics.OverlapBoxNonAlloc(t.TransformPoint(box.center), half, OverlapBuffer,
                    t.rotation, Physics.AllLayers, q);
            }
            Bounds b = trigger.bounds;
            return Physics.OverlapBoxNonAlloc(b.center, b.extents, OverlapBuffer, Quaternion.identity,
                Physics.AllLayers, q);
        }

        /// <summary>
        /// The local guest's body: anything under a PlayerRig that is not a remote
        /// player's avatar, plus tagged/layered player stand-ins (playtest probes) —
        /// the same signals as dp.is_player.
        /// </summary>
        internal static bool IsLocalPlayerCollider(Collider other)
        {
            GameObject go = other.gameObject;
            if (go.GetComponentInParent<RemotePlayer>(true) != null) return false;
            return DreamParkLuaAPI.IsPlayerObject(go);
        }

        private bool CanPress(Collider other)
        {
            if (pressedBy == Pressers.AnyPlayerCollider || IsHandCollider(other)) return true;
            // A rig that ships no hand colliders could otherwise never press.
            PlayerRig rig = other.GetComponentInParent<PlayerRig>(true);
            return rig != null && !RigHasHandColliders(rig);
        }

        internal static bool IsHandCollider(Collider other)
        {
            if (other.GetComponentInParent<HandTracker>(true) != null) return true;
#if UNITY_EDITOR
            var probe = other.GetComponent<global::DreamPark.Playtest.PlaytestProbe>();
            if (probe != null) return probe.part != global::DreamPark.Playtest.PlaytestProbe.Part.Body;
#endif
            // The phone playtest agent's hand probes ("AgentProbe LeftHand").
            string n = other.gameObject.name;
            return n.EndsWith("LeftHand") || n.EndsWith("RightHand");
        }

        private static bool RigHasHandColliders(PlayerRig rig)
        {
            foreach (HandTracker hand in rig.GetComponentsInChildren<HandTracker>(true))
                if (hand.GetComponentInChildren<Collider>(true) != null) return true;
            return false;
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
