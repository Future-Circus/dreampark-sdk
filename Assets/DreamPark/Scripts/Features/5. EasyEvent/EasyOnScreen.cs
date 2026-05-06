namespace DreamPark.Easy
{
    using UnityEngine;

    public class EasyOnScreen : EasyEvent
    {
        public float margin = 0.3f;
        public float maxDistance = 100f;
        public float marginVertical
        {
            get
            {
                return margin * Screen.height;
            }
        }
        public float marginHorizontal
        {
            get
            {
                return margin * Screen.width;
            }
        }

        public override void OnEvent(object arg0 = null)
        {
            isEnabled = true;
        }

        public virtual void Update()
        {
            if (!isEnabled)
            {
                return;
            }

            // Check if there is a main camera
            if (Camera.main == null)
            {
                Debug.LogWarning("[EasyOnScreen] No main camera found in scene.");
                return;
            }

            if (isOnScreen && isInRange)
            {
                Debug.Log(gameObject.name + " " + typeof(EasyOnScreen).Name + " isOnScreen and isInRange");
                isEnabled = false;
                if (onEvent != null)
                {
                    onEvent.Invoke(null);
                }
            }
        }

        public bool isOnScreen {
            get {
                Vector3 screenPos = Camera.main.WorldToScreenPoint(transform.position);
                // Only trigger if object is visible and within margins
                return screenPos.z > 0 &&                                    // not behind camera
                screenPos.y > marginVertical &&                       // inside top margin
                screenPos.y < Screen.height - marginVertical &&
                screenPos.x > marginHorizontal &&                     // inside side margin
                screenPos.x < Screen.width - marginHorizontal;
            }
        }

        public bool isInRange {
            get {
                return Vector3.Distance(Camera.main.transform.position, transform.position) < maxDistance;
            }
        }
    }

}
