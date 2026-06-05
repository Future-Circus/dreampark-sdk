namespace DreamPark.Easy
{
    using UnityEngine;

    public class EasyTarget : EasyEvent
    {
        public bool targetPlayer = true;
        [HideIf("detectPlayer")] public bool targetObject = false;
        [HideIf("targetPlayer")][ShowIf("targetObject")] public Transform target;
        [HideIf("targetPlayer")] public bool targetTag = false;
        [HideIf("targetPlayer")][ShowIf("targetTag")] public string tag = "Player";

        private GameObject _target;
        private float cooldown = 0f;
        public override void OnEvent(object arg0 = null) {
            if (targetPlayer) {
                _target = Camera.main.gameObject;
            } else if (targetObject) {
                _target = target.gameObject;
            }
            isEnabled = true;
        }
        public void Update()
        {
            if (!isEnabled) {
                return;
            }
            if (cooldown > 0f) {
                cooldown -= Time.deltaTime;
                return;
            }
            if (targetTag && _target == null) {
                _target = GameObject.FindGameObjectWithTag(tag);
            }
            if (_target != null) {
                OnEventDisable();
                onEvent?.Invoke(_target);
            }
        }
    }
}