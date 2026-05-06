namespace DreamPark.Easy
{
    using UnityEngine;

    public class EasyOffScreen : EasyOnScreen {
        public override void Update() {
            if (!isEnabled) {
                return;
            }
            if (!isOnScreen || !isInRange) {
                isEnabled = false;
                onEvent?.Invoke(null);
            }
        }
    }
}
