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
                Destroy(gameObject);
                return;
            }
            instances.Add(gameId, this);
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
            if (instances != null && instances.ContainsKey(gameId)) {
                instances.Remove(gameId);
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