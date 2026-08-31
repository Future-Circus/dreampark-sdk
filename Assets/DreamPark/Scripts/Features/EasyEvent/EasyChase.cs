namespace DreamPark.Easy
{
    using System.Collections;
    using UnityEngine;
    using UnityEngine.AI;

    public class EasyChase : EasyEvent
    {
        public NavMeshAgent agent;
        public Transform target;
        public float speed = 10f;
        public float stoppingDistance = 0.05f;
        public float turnSpeed = 1f;
        public bool waitUntilDestinationReached = true;
        private Rigidbody rb;
        #if UNITY_EDITOR
        [Space(10)]
        [ReadOnly] public bool agentEnabled = false;
        [ReadOnly] public bool agentOnNavMesh = false;
        [ReadOnly] public bool agentIsOnNavMesh = false;
        #endif
        public override void Awake () {
            base.Awake();
            agent = GetComponent<NavMeshAgent>();
            if(agent != null)
            {
                agent.speed = speed;
                agent.stoppingDistance = stoppingDistance;
                agent.angularSpeed = turnSpeed;
            }
            rb = GetComponent<Rigidbody>();
        }
        public override void Start() {
            base.Start();
            if (!waitUntilDestinationReached) {
                onEvent?.Invoke(null);
            }
        }

        IEnumerator WakeUp () {
            agent.enabled = false;
            yield return new WaitForSeconds(0.1f);
            agent.enabled = true;
        }

        IEnumerator FallToNavMesh()
        {
            agent.enabled = false;
            Quaternion startRot = transform.rotation;
            Vector3 direction = (new Vector3(target.position.x, 0, target.position.z) - new Vector3(transform.position.x, 0, transform.position.z)).normalized;
            Quaternion targetRot = Quaternion.LookRotation(direction, Vector3.up);

            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 10f, NavMesh.AllAreas))
            {
                Vector3 target = hit.position;
                float fallTime = 0.4f;
                Vector3 start = transform.position;
                float t = 0f;
                while (t < 1f)
                {
                    t += Time.deltaTime / fallTime;
                    float y = Mathf.Sin(t * Mathf.PI) * 1f; // nice arc
                    transform.position = Vector3.Lerp(start, target, t) + Vector3.up * y;
                    transform.rotation = Quaternion.Slerp(startRot, targetRot, t);
                    yield return null;
                }
            }
            agent.enabled = true;
            agent.isStopped = false;
        }

        public override void OnEvent(object arg0 = null) {
            Debug.Log("[EasyChase] OnEvent called " + stoppingDistance);
            isEnabled = true;
            if (rb != null) {
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.constraints = RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionZ | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY | RigidbodyConstraints.FreezeRotationZ;
                rb.WakeUp();
            }
            if(agent != null)
            {
                agent.speed = speed;
                agent.stoppingDistance = stoppingDistance;
                agent.angularSpeed = turnSpeed;
            }
            if (arg0 is GameObject gameObject) {
                target = gameObject.transform;
            }
            if (target == null) {
                target = Camera.main.transform;
            }
            if (agent != null) {
                 if (!agent.IsOnNavMesh(out NavMeshHit _)) {
                    StartCoroutine(FallToNavMesh());
                } else {
                    agent.enabled = true;
                    agent.isStopped = false;
                }
            }
        }

        void Update() {
            #if UNITY_EDITOR
                agentEnabled = agent != null && agent.enabled;
                agentOnNavMesh = agent != null && agent.isOnNavMesh;
                agentIsOnNavMesh = agent != null && agent.IsOnNavMesh(out NavMeshHit _);
            #endif

            if (!isEnabled) {
                return;
            }
            Vector3 targetPosition = target.position;

            if (agent == null) {
                var _targetPosition = transform.parent.InverseTransformPoint(targetPosition);
                transform.localPosition = Vector3.MoveTowards(transform.localPosition, new Vector3(_targetPosition.x, transform.localPosition.y, _targetPosition.z), speed * Time.deltaTime);
                Vector3 flatDirection = Vector3.ProjectOnPlane(_targetPosition - transform.localPosition, Vector3.up);
                if (flatDirection.sqrMagnitude > 0.001f)
                {
                    transform.localRotation = Quaternion.Lerp(transform.localRotation, Quaternion.LookRotation(flatDirection, Vector3.up), turnSpeed * Time.deltaTime);
                }
            } else if (agent != null && agent.enabled && agent.isOnNavMesh && agent.IsOnNavMesh(out NavMeshHit _)) {
                Debug.Log("[EasyChase] Setting destination to " + targetPosition);
                agent.SetDestination(targetPosition);
            }
            // catch!
            Debug.Log(gameObject.name + " : " + agent.remainingDistance + " Stopping distance: " + stoppingDistance);
            Debug.Log("transform.position.Distance(targetPosition): " + transform.position.Distance(targetPosition));
            if ((agent != null && agent.isOnNavMesh && agent.IsOnNavMesh(out NavMeshHit _) && agent.remainingDistance != Mathf.Infinity && agent.remainingDistance != 0f && agent.remainingDistance < stoppingDistance) || (agent == null && transform.position.Distance(targetPosition) < stoppingDistance)) {
                agent.isStopped = true;
                agent.enabled = false;
                OnEventDisable();
                if (waitUntilDestinationReached) {
                    onEvent?.Invoke(null);
                }
            }
        }
        public override void OnEventDisable() {
            base.OnEventDisable();
            if (agent != null) {
                if (agent.isOnNavMesh && agent.IsOnNavMesh(out NavMeshHit _)) {
                    agent.isStopped = true;
                }
                agent.enabled = false;
            }
        }
    }
}
