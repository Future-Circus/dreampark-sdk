namespace DreamPark.Easy
{
    #if UNITY_EDITOR
    using UnityEditor;
    #endif
    using UnityEngine;

    public class EasyLookAt : EasyEvent
    {
        public Transform target;
        public Vector3 rotationOffset = Vector3.zero;
        public float speed = -1f;
        public bool rotateX = true;
        public bool rotateY = true;
        public bool rotateZ = true;
        public bool lateUpdate = false;
        private Quaternion lastRotation;
        public void UpdateEvent() {
            if (!isEnabled) {
                return;
            }
            if (target == null) {
                target = Camera.main.transform;
            }
            Vector3 targetPosition = target.position;
            if (target != null) {
                if (rotateX && rotateY && rotateZ && speed <= 0f && rotationOffset == Vector3.zero) {
                    transform.LookAt(targetPosition);
                } else {
                    Quaternion targetRotation = Quaternion.LookRotation(targetPosition - transform.position) * Quaternion.Euler(rotationOffset);
                    if (!rotateX) {
                        targetRotation.x = transform.rotation.x;
                    }
                    if (!rotateY) {
                        targetRotation.y = transform.rotation.y;
                    }
                    if (!rotateZ) {
                        targetRotation.z = transform.rotation.z;
                    }
                    if (speed > 0f) {
                        transform.rotation = Quaternion.Lerp(lastRotation, targetRotation, speed * Time.deltaTime);
                    } else {
                        transform.rotation = targetRotation;
                    }
                    lastRotation = transform.rotation;
                }
            }
        }

        public void Update() {
            if (!isEnabled) {
                lastRotation = transform.rotation;
                return;
            }
            UpdateEvent();
        }

        public void LateUpdate() {
            if (!isEnabled) {
                lastRotation = transform.rotation;
                return;
            }
            if (lateUpdate) {
                UpdateEvent();
            }
        }
    }

}
