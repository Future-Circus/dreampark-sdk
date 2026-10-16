using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Internal contact relay for <see cref="NetBody"/>: Unity only reports collisions to
    /// scripts on the Rigidbody's GameObject, so dp.net_body attaches this hidden helper at
    /// runtime. Never authored, never in a prefab, nothing to configure.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class NetBodyContacts : MonoBehaviour
    {
        [System.NonSerialized] public NetBody body;

        void OnCollisionEnter(Collision c) { if (body != null && c != null) body.OnContact(c.collider); }
        void OnCollisionStay(Collision c) { if (body != null && c != null) body.OnContact(c.collider); }
    }
}
