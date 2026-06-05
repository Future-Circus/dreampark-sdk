using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DreamPark {
    public class HeadTracker : MonoBehaviour
    {
        //we have to fake feet tracking by using the head position and velocity
        public Transform head;
        public Rigidbody rb;

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
        }

        void UpdateStep () {
            if (rb) {
                rb.position = head.position;
                rb.rotation = head.rotation;
            } else {
                transform.position = head.position;
                transform.rotation = head.rotation;
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