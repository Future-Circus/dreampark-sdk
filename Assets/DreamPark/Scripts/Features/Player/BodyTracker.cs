using System;
using UnityEngine;
using UnityEngine.Events;

namespace DreamPark {
    public class BodyTracker : MonoBehaviour
    {
        /// <summary>
        /// External torso anchor. When something upstream actually knows where the
        /// player's torso is, it assigns this and the tracker follows it verbatim.
        ///
        /// Why it exists: everything below fakes a torso from the HEAD, because on
        /// Quest `Camera.main` IS the player's head and that is the only signal
        /// available. On a phone `Camera.main` is the phone, so the fake lands on
        /// the device instead of the person — and unlike HeadTracker/FeetTracker
        /// (which both expose a `head` field) there was no way to tell this tracker
        /// otherwise. ARBodySource sets this from ARKit's spine/hip joints on iOS.
        ///
        /// Null (the default, and the entire Quest path) = unchanged behaviour:
        /// head position + <see cref="yOffset"/>. When set, <see cref="yOffset"/> is
        /// NOT applied — the anchor is already the torso, not a head to offset from
        /// — and <see cref="overrideYOffset"/> is the per-rig nudge instead.
        /// </summary>
        [Tooltip("External torso anchor (ARKit body tracking on iOS). Null = follow the head like always.")]
        public Transform anchorOverride;

        [Tooltip("Nudge applied ONLY when anchorOverride is set. yOffset is skipped in that case because the anchor is already a torso.")]
        public float overrideYOffset = 0f;

        Transform headAnchor;
        private Rigidbody rb;

        public float yOffset = 0.75f;

        /// <summary>True while an external anchor is driving this tracker.</summary>
        public bool isDrivenExternally => anchorOverride != null;

        void Awake()
        {
            headAnchor = Camera.main?.transform;
        }

        private void Start()
        {
            rb = GetComponent<Rigidbody>();
        }

        void UpdateStep () {

            // The override wins outright and needs no Camera.main at all — a phone
            // build must not fall back to the device pose just because the AR
            // camera went away mid-frame.
            if (anchorOverride != null) {
                Vector3 overridePosition = anchorOverride.position + new Vector3(0, overrideYOffset, 0);
                if (rb != null) {
                    rb.position = overridePosition;
                } else {
                    transform.position = overridePosition;
                }
                return;
            }

            if (headAnchor == null) {
                headAnchor = Camera.main?.transform;
                return;
            }

            if (rb != null) {
                rb.position = headAnchor.position + new Vector3(0, yOffset, 0);
            } else {
                transform.position = headAnchor.position + new Vector3(0, yOffset, 0);
            }


        }
        void LateUpdate()
        {
            UpdateStep();
        }
        void Update()
        {
            UpdateStep();
        }
        void FixedUpdate()
        {
            UpdateStep();
        }
    }
}
