using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DreamPark {
    public class FeetTracker : MonoBehaviour
    {
        //we have to fake feet tracking by using the head position and velocity
        public Transform head;
        public Rigidbody rb;
        public float yOffset = 0;
        public float kickMultiplier = 1f;
        private Vector3 lastPosition;
        private List<Vector3> positionSamples = new List<Vector3>();

        void Start()
        {
            if (!head)
            {
                head = Camera.main.transform;
            }
            if (!rb)
            {
                rb = GetComponent<Rigidbody>();
            }
            lastPosition = head.position;
        }

        void FixedUpdate()
        {
            Vector3 positionXZ = new Vector3(transform.position.x, 0, transform.position.z);
            positionSamples.Add(positionXZ-lastPosition);
            lastPosition = positionXZ;
            if (positionSamples.Count > 30) {
                positionSamples.RemoveAt(0);
            }
        }

        void LateUpdate () {
            if (rb != null) {
                rb.position = new Vector3(head.position.x, head.position.y + yOffset, head.position.z) + GetDirection() * Mathf.Clamp(kickMultiplier*GetVelocity(10f),0f,0.2f);
                if (GetVelocity() > 0.01f) {
                    rb.rotation = Quaternion.LookRotation(GetDirection());
                }
            } else {
                transform.position = new Vector3(head.position.x, head.position.y + yOffset, head.position.z) + GetDirection() * Mathf.Clamp(kickMultiplier*GetVelocity(10f),0f,0.2f);
                if (GetVelocity() > 0.01f) {
                    transform.rotation = Quaternion.LookRotation(GetDirection());
                }
            }
        }

        public Vector3 GetDirection () {
            return positionSamples.Count > 0 ? positionSamples.Aggregate((a, b) => a + b).normalized : Vector3.forward;
        }

        public float GetVelocity (float multiplier = 1f) {
            return positionSamples.Count > 0 ? multiplier*positionSamples.Aggregate((a, b) => a + b).magnitude / positionSamples.Count : 0f;
        }

        public void OnDrawGizmos()
        {
            if (!head)
                return;
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(head.position, GetDirection()*GetVelocity());
        }
    }
}
