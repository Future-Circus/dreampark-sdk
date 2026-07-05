using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DreamPark {
    public class PlayerRig : MonoBehaviour
    {
        public static Dictionary<string, PlayerRig> instances;
        public static PlayerRig Instance;
        [ReadOnly] public string gameId;

        void Awake() {
            if (instances == null) {
                instances = new Dictionary<string, PlayerRig>();
            }
            if (instances.ContainsKey(gameId)) {
                // Should NOT happen in the normal path now that ContentManager's
                // claim guard stops duplicate loads before instantiation. If you see
                // this on device, a duplicate slipped through and the dedup backstop
                // caught it — worth investigating the load site.
                Debug.Log($"[PlayerRig] DUPLICATE rig for gameId '{gameId}' — destroying (instances stays {instances.Count})");
                Destroy(gameObject);
                return;
            }
            instances.Add(gameId, this);
            Debug.Log($"[PlayerRig] Registered rig for gameId '{gameId}' (instances={instances.Count})");
#if DREAMPARKCORE
            if (ContentManager.contentDependencies != null) {
                List<string> contentIds = ContentManager.contentDependencies.Where(x => x.Value.Contains(gameId)).Select(x => x.Key).ToList();
                if (contentIds != null && contentIds.Count > 0) {
                    foreach (var contentId in contentIds) {
                        if (!instances.ContainsKey(contentId)) {
                            instances.Add(contentId, this);
                        }
                    }
                }
            }
#endif
        }

        void Start() {
            if (Instance == null) {
                Instance = this;
            } else {
                gameObject.SetActive(false);
            }
        }

        public void Show() {
            if (Instance != this) {
                Instance.Hide();
            }
            Instance = this;
            if (gameObject.activeSelf) {
                return;
            }
            gameObject.SetActive(true);
        }

        public void Hide() {
            gameObject.SetActive(false);
        }

        public void OnDestroy() {
            // Only remove registrations that actually point at THIS rig. The
            // dictionary holds the primary gameId key plus any contentId aliases
            // added in Awake, and multiple rigs can share a gameId. Removing by
            // key alone (the old behaviour) meant a destroyed *duplicate* would
            // yank the surviving rig's entry out of the dictionary, re-opening
            // the dedup gate so the next load survived too — i.e. a rig per
            // attraction. Match on value so a duplicate's teardown never
            // deregisters the rig that's keeping the slot.
            if (instances != null) {
                var ownedKeys = instances.Where(kv => kv.Value == this)
                                         .Select(kv => kv.Key)
                                         .ToList();
                foreach (var key in ownedKeys) {
                    instances.Remove(key);
                }
                if (ownedKeys.Count > 0) {
                    Debug.Log($"[PlayerRig] Registered rig for gameId '{gameId}' destroyed — removed {ownedKeys.Count} key(s) (instances={instances.Count})");
                }
            }
            if (Instance == this) {
                Instance = null;
            }
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.white;
            Mesh humanMesh = Resources.Load<Mesh>("Meshes/HumanReference");
            Gizmos.DrawMesh(humanMesh, new Vector3(0, -1.6f, 0));
            Gizmos.matrix = oldMatrix;
        }
    #endif
    }
}