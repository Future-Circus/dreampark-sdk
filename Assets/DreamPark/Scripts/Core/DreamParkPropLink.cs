using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Runtime-only bookkeeping for dp.spawn_prop (DreamParkLuaProps.cs). On a spawned prop
    /// root it carries the handle id, so destroying the prop by any route (Destroy, its
    /// attraction unloading, a Sequence level swap) retires the handle. On an owner
    /// (isOwner, the calling attraction root or the calling object) its OnDestroy despawns
    /// every prop that owner spawned, wherever they were parented. Never authored.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    [ExecuteAlways]   // OnDestroy must also retire handles of edit-mode spawns (tests, tooling)
    public sealed class DreamParkPropLink : MonoBehaviour
    {
        [Tooltip("dp.spawn_prop handle id of this prop (0 = none / already despawned).")]
        public int propId;
        [Tooltip("This object owns props spawned by its scripts.")]
        public bool isOwner;

        void OnDestroy()
        {
            if (propId != 0) { int id = propId; propId = 0; DreamParkLuaProps.NoteDestroyed(id); }
            if (isOwner) DreamParkLuaProps.NoteOwnerDestroyed(gameObject);
        }
    }
}
