using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace DreamPark.Easy
{
    public class EasyThrow : EasyEvent
    {
        public string[] targetLayerOrder;
        public string[] targetTagOrder;
        public GameObject target;
        public float upwardMin = 0.0f;
        private Rigidbody rb;
        private LayerMask priorityLayerMask;
        private LayerMask fallbackLayerMask;
        private string targetTagMask;
        private NavMeshAgent agent;
        public float forceMultiplier = 2.0f;
        public override void Awake()
        {
            base.Awake();
            rb = GetComponent<Rigidbody>();
            agent = GetComponent<NavMeshAgent>();
        }
        public override void Start()
        {
            base.Start();
            if (targetLayerOrder != null && targetLayerOrder.Length > 0)
            {
                priorityLayerMask = 1 << LayerMask.NameToLayer(targetLayerOrder[0]);
                fallbackLayerMask = 0;

                if (targetLayerOrder.Length > 1)
                {
                    for (int i = 1; i < targetLayerOrder.Length; i++)
                    {
                        int layer = LayerMask.NameToLayer(targetLayerOrder[i]);
                        if (layer >= 0)
                            fallbackLayerMask |= (1 << layer);
                    }
                }
            }
            else
            {
                priorityLayerMask = ~0;
            }
            if (targetTagOrder != null && targetTagOrder.Length > 0)
            {
                targetTagMask = targetTagOrder.Aggregate((a, b) => a + "," + b);
            }
        }
        public override void OnEvent(object arg0 = null)
        {
            CollisionWrapper lastCollision = GetLastCollision(arg0);
            if (lastCollision == null)
            {
                Debug.Log("[EasyThrow] No collision data found!");
                Throw(transform.forward, 1f);
                onEvent?.Invoke(null);
                return;
            }
            Throw(transform.position - lastCollision.contactPoint, lastCollision.relativeVelocity.magnitude);
            onEvent?.Invoke(null);
        }

        public CollisionWrapper GetLastCollision(object arg0 = null)
        {
            if (arg0 is CollisionWrapper _lastCollision)
            {
                return _lastCollision;
            }
            else if (Componentizer.DoComponent<EasyVars>(gameObject, true).lastCollision != null)
            {
                return Componentizer.DoComponent<EasyVars>(gameObject, true).lastCollision;
            }
            return null;
        }

        public void Throw(Vector3 impactDirection, float impactForce = 1f)
        {
            rb.freezeRotation = false;
            rb.isKinematic = false;
            rb.constraints = RigidbodyConstraints.None;
            rb.useGravity = true;
            rb.WakeUp();

            if (agent != null)
            {
                agent.enabled = false;
            }

            if (target != null)
            {
                if (rb.LaunchAtTarget(target))
                {
                    return;
                }
            }
            else if (targetTagOrder != null && targetTagOrder.Length > 0)
            {
                if (rb.LaunchAtLayerWithTag(priorityLayerMask, targetTagMask, out GameObject bestTarget, 20f, upwardMin))
                {
                    return;
                }
            }
            else if (targetLayerOrder != null && targetLayerOrder.Length > 0)
            {
                if (rb.LaunchAtLayer(priorityLayerMask, 20f, fallbackLayerMask, 20f, upwardMin))
                {
                    return;
                }
            }

            Debug.Log(gameObject.name + "- no target found, launching with direction force");

            gameObject.tag = "ActiveHit";

            impactDirection.Normalize();

            Vector3 hittedForce = impactDirection * impactForce * forceMultiplier;
            hittedForce = new Vector3(hittedForce.x, Mathf.Max(upwardMin, hittedForce.y), hittedForce.z);

            rb.AddForce(hittedForce * rb.mass, ForceMode.Impulse);

            Vector3 randomTorque = new Vector3(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            ) * forceMultiplier;
            rb.AddTorque(randomTorque, ForceMode.Impulse);
        }
    }

}