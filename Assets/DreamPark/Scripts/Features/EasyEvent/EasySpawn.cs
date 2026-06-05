using UnityEngine;
namespace DreamPark.Easy {
    public class EasySpawn : EasyEvent
    {
        public GameObject spawnPrefab;
        public Transform spawnPoint;
        [Range(1, 10)] public int amount = 1;
        public bool copyRotation = true;
        public bool waitForDestroyed = false;

        private int pendingDestroyCount = 0;

        public override void Awake() {
            base.Awake();
            if (spawnPoint == null) {
                spawnPoint = transform;
            }
        }

        public override void OnEvent(object arg0 = null) {
            if (spawnPrefab != null) {
                pendingDestroyCount = 0;

                if (amount == 1) {
                    Spawn(spawnPrefab, spawnPoint);
                } else {
                    for (int i = 0; i < amount; i++) {
                        Spawn(spawnPrefab, spawnPoint, i);
                    }
                }

                if (!waitForDestroyed) {
                    onEvent?.Invoke(null);
                }
            }
        }

        private void OnSpawnedObjectDestroyed() {
            pendingDestroyCount--;
            if (pendingDestroyCount <= 0 && waitForDestroyed) {
                onEvent?.Invoke(null);
            }
        }

        public virtual GameObject Spawn(GameObject prefab, Transform point, int index = 0) {
            GameObject spawnedObject = Instantiate(prefab);
            spawnedObject.SetActive(true);
            spawnedObject.transform.position = point.position;
            if (copyRotation) {
                spawnedObject.transform.rotation = point.rotation;
            }

            if (waitForDestroyed) {
                pendingDestroyCount++;
                var notifier = spawnedObject.AddComponent<DestroyNotifier>();
                notifier.onDestroyed = OnSpawnedObjectDestroyed;
            }

            return spawnedObject;
        }
    }

    /// <summary>
    /// Helper component that notifies when its GameObject is destroyed.
    /// </summary>
    public class DestroyNotifier : MonoBehaviour
    {
        public System.Action onDestroyed;

        private void OnDestroy() {
            onDestroyed?.Invoke();
        }
    }
}