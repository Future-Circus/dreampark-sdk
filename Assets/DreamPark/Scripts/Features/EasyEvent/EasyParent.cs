namespace DreamPark.Easy
{
    using UnityEngine;

    public class EasyParent : EasyEvent
    {
        public Transform parent;
        public bool recenter = false;

        public override void OnEvent(object arg0 = null)
        {
            // Use arg0 as parent if provided
            Transform targetParent = parent;
            if (arg0 is GameObject go)
                targetParent = go.transform;
            else if (arg0 is Transform t)
                targetParent = t;

            transform.SetParent(targetParent, true);
            if (recenter) {
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
            }
            onEvent?.Invoke(null);
        }
    }

}
