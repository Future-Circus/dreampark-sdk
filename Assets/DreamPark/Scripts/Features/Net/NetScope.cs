using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Stable identity boundary for NetId hashing. The park spawner
    /// (LevelAnchor.Spawn in core) stamps this on every spawned attraction
    /// root with a key derived ONLY from park-doc data that is identical on
    /// every client: "{levelId}|{objectIndex}|{resourceName}".
    ///
    /// NetId.ComputeId walks up the hierarchy and STOPS at the first
    /// NetScope, mixing in scopeKey instead of continuing to the scene root.
    /// Everything above an attraction root (LevelAnchor / SubLevelRoot /
    /// PortalAnchor sibling order) depends on async download completion
    /// order and is therefore different on every device — it must never
    /// enter the hash. Everything below (inside the prefab) is defined by
    /// the prefab asset and identical everywhere.
    /// </summary>
    public class NetScope : MonoBehaviour
    {
        [Tooltip("Park-doc-stable identity for this spawned subtree. Set by the spawner; identical on every client in the park.")]
        public string scopeKey;
    }
}
