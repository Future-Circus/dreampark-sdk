namespace DreamPark.Easy
{
    using Oculus.Interaction.PoseDetection;
    using UnityEngine;
    using UnityEngine.AI;

    public class EasyEnemy : EasyEvent
    {
        [Range(1, 10)] public int hitpoints = 3;
        public EasyEvent onHit;
        public EasyEvent onDeath;
        private bool isDead = false;
        public override void Start() {
            if (eventOnStart) {
                onEvent?.Invoke(null);
            }
        }
        public override void OnEvent(object arg0 = null)
        {
            if (isDead) {
                return;
            }
            if (arg0 is int) {
                hitpoints -= (int)arg0;
            } else {
                hitpoints--;
            }
            EasyEvent[] easyEvents = GetComponents<EasyEvent>();
            foreach (var easyEvent in easyEvents) {
                easyEvent.OnEventDisable();
            }
            if (hitpoints <= 0) {
                isDead = true;
                if (TryGetComponent<NavMeshAgent>(out var agent)) {
                    agent.enabled = false;
                }
                if (onDeath != null) {
                    onDeath?.OnEvent(null);
                } else {
                    onEvent?.Invoke(null);
                }
            } else {
                if (onHit != null) {
                    onHit?.OnEvent(null);
                } else {
                    onEvent?.Invoke(null);
                }
            }
        }
    }

}
