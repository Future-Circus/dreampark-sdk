#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.Playtest
{
    /// <summary>
    /// One physics probe on the playtest avatar (a hand sphere or the body
    /// capsule). Kinematic Rigidbody + collider, re-parented under whichever
    /// PlayerRig is active so dp.is_player(other) is true for it by ancestry —
    /// exactly how content-authored rig colliders are recognised. With no rig in
    /// the scene it falls back to the Player layer, the other thing is_player
    /// accepts. Contacts are reported to <see cref="PlaytestObserver"/> so the
    /// agent learns what it actually touched.
    /// </summary>
    [AddComponentMenu("")]
    public class PlaytestProbe : MonoBehaviour
    {
        public enum Part { LeftHand, RightHand, Body }

        public Part part;
        Rigidbody _rb;
        CapsuleCollider _capsule;
        readonly Dictionary<int, float> _lastContact = new Dictionary<int, float>();

        public static PlaytestProbe Create(string name, Part part, float radius, float bodyHeight)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            var probe = go.AddComponent<PlaytestProbe>();
            probe.part = part;

            probe._rb = go.AddComponent<Rigidbody>();
            probe._rb.isKinematic = true;
            probe._rb.useGravity = false;
            probe._rb.interpolation = RigidbodyInterpolation.None;
            probe._rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            if (part == Part.Body)
            {
                probe._capsule = go.AddComponent<CapsuleCollider>();
                probe._capsule.radius = radius;
                probe._capsule.direction = 1;
                probe.SetBodyHeight(bodyHeight);
            }
            else
            {
                var sphere = go.AddComponent<SphereCollider>();
                sphere.radius = radius;
            }
            probe.EnsureParent();
            return probe;
        }

        public string Label => part == Part.LeftHand ? "left hand" : part == Part.RightHand ? "right hand" : "body";

        public void SetBodyHeight(float eyeHeight)
        {
            if (_capsule == null) return;
            // Floor to just under the eyes, so floor pads (a Start Button is ~0.2 m
            // tall) register when the bot steps onto them.
            _capsule.height = Mathf.Max(eyeHeight - 0.05f, _capsule.radius * 2f);
        }

        void EnsureParent()
        {
            var rig = PlayerRig.Instance;
            Transform wanted = rig != null && rig.isActiveAndEnabled ? rig.transform : null;
            if (transform.parent == wanted) return;
            transform.SetParent(wanted, true);
            gameObject.layer = wanted != null ? 0 : LayerMask.NameToLayer("Player");
            // Undo inherited scale so a probe is the same size in any LevelAnchor.
            Vector3 s = wanted != null ? wanted.lossyScale : Vector3.one;
            transform.localScale = new Vector3(1f / Mathf.Max(s.x, 1e-4f), 1f / Mathf.Max(s.y, 1e-4f), 1f / Mathf.Max(s.z, 1e-4f));
        }

        public void Follow(Vector3 world, bool teleport)
        {
            EnsureParent();
            if (teleport || !Application.isPlaying) transform.position = world;
            else _rb.MovePosition(world);
        }

        void OnTriggerEnter(Collider other) => Report(other, "entered trigger");
        void OnCollisionEnter(Collision c) => Report(c.collider, "bumped");

        void Report(Collider other, string verb)
        {
            if (other == null || PlaytestAvatar.IsPlayerCollider(other)) return;
            int id = other.GetInstanceID();
            if (_lastContact.TryGetValue(id, out float last) && Time.time - last < 0.5f) return;
            _lastContact[id] = Time.time;
            PlaytestObserver.RecordContact(this, other, verb);
        }
    }
}
#endif
