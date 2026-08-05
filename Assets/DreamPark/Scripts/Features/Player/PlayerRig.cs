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
            // A rig whose bundle finished AFTER the zone it belongs to was entered has
            // to claim itself.
            //
            // GameArea.Enter() is the only thing that ever calls Show(), it looks the
            // rig up exactly once, and it cannot run again until the player physically
            // walks out of the zone and back in (Enter's first line is
            // `if (currentGameArea == this) return`). The attraction and the rig are
            // different addressables, so "attraction lands first, player is already
            // standing in the footprint" is an ordinary outcome — and when it happened
            // the lookup missed, then this method saw Instance != null and switched the
            // real rig OFF. The player finished the session inside the attraction
            // wearing the PREVIOUS attraction's rig, and dp.player() handed every Lua
            // script the wrong GameObject.
            //
            // Same law as the sticky relays: Enter() is the edge, currentGameArea is
            // the state, and anything arriving late reads the state instead of waiting
            // for an edge that already passed.
            if (ClaimsCurrentZone()) {
                Show();
                return;
            }
            if (Instance == null) {
                Instance = this;
            } else {
                gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// True when the zone the player is standing in RIGHT NOW is one this rig
        /// serves. Checked against `instances`, so contentId aliases registered in
        /// Awake count too.
        /// </summary>
        bool ClaimsCurrentZone() {
            var zone = GameArea.currentGameArea;
            if (zone == null || !zone.isPlaying || string.IsNullOrEmpty(zone.gameId)) return false;
            if (instances == null) return false;
            return instances.TryGetValue(zone.gameId, out var rig) && rig == this;
        }

        public void Show() {
            // Instance is a plain static that OnDestroy nulls out, so a GameArea.Enter()
            // landing after an attraction unload used to NullReference here — from
            // inside Update, which takes the whole zone loop down with it.
            if (Instance != null && Instance != this) {
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
            Gizmos.color = new Color(0.96f, 0.91f, 0.75f, 0.2f);
            Mesh humanMesh = Resources.Load<Mesh>("Meshes/HumanReference");
            Gizmos.DrawMesh(humanMesh, new Vector3(0, -1.6f, 0));
            Gizmos.matrix = oldMatrix;
        }
    #endif
    }
}