using System;
using UnityEngine;
using UnityEngine.Events;

namespace DreamPark {
    public class BodyTracker : MonoBehaviour
    {
        Transform headAnchor;
        private Rigidbody rb;

        public float yOffset = 0.75f;

        void Awake()
        {
            headAnchor = Camera.main?.transform;
        } 

        private void Start()
        {
            rb = GetComponent<Rigidbody>();
        }

        void UpdateStep () {
            
            if (headAnchor == null) {
                headAnchor = Camera.main?.transform;
                return;
            }

            if (rb != null) {
                rb.position = headAnchor.position + new Vector3(0, yOffset, 0);
            } else {
                transform.position = headAnchor.position + new Vector3(0, yOffset, 0);
            }

        
        }
        void LateUpdate()
        {
            UpdateStep();
        }
        void Update()
        {
            UpdateStep();
        }
        void FixedUpdate()
        {
            UpdateStep();
        }
    }
}