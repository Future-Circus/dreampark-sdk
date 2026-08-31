namespace DreamPark.Easy
{
    using UnityEngine;

    public class EasySpin : EasyEvent
    {
        public enum SpinType
        {
            ROTATION, // Rotate TO a specific rotation (absolute)
            DELTA, // Rotate a relative amount from current rotation (delta euler)
            LOOK_AT
        }
        public enum SpinDuration
        {
            TIME, // Interpolate over a specific duration
            SPEED // Rotate at a constant speed (degrees/sec, uses .magnitude)
        }
        public SpinType spinType = SpinType.ROTATION;
        public SpinDuration spinDuration = SpinDuration.TIME;
        [ShowIf("spinType", SpinType.ROTATION)] public Vector3 eulerAngles;
        [ShowIf("spinType", SpinType.DELTA)] public Vector3 eulerDelta;
        [ShowIf("spinDuration", SpinDuration.SPEED)] public float speed = 90f;
        [ShowIf("spinDuration", SpinDuration.TIME)] public float time = 1f;
        [ShowIf("spinType", SpinType.LOOK_AT)] public Transform target;
        [ShowIf("spinType", SpinType.LOOK_AT)] public bool freezeX = false;
        [ShowIf("spinType", SpinType.LOOK_AT)] public bool freezeY = false;
        [ShowIf("spinType", SpinType.LOOK_AT)] public bool freezeZ = false;
        private float startTime = 0f;
        private Quaternion startRotation;
        private Quaternion targetRotation;
        private Vector3 spinDeltaEuler; // For full spins, to accumulate rotation
        private float spinAccum = 0f;    // How far (in degrees) we've spun along intended euler axis
        private float spinTargetDegrees = 0f;
        private Vector3 deltaAxis;
        private float deltaAngle;
        private float rotatedSoFar = 0f;
        private bool isFullRotation = false; // means "force always spin all the way around"
        public bool delayNextEvent = true;

        public override void OnEvent(object arg0 = null)
        {
            isEnabled = true;
            startTime = Time.time;
            startRotation = transform.localRotation;
            rotatedSoFar = 0f;
            spinAccum = 0f;
            isFullRotation = false;

            if (spinType == SpinType.ROTATION)
            {
                // Simple "full spin" detection: If any axis exactly 360, perform a full spin on that axis,
                // regardless of current rotation. Allow multiple axes simultaneously.
                // (For Time mode, this is tricky, but makes more sense for speed mode.)
                // Accept -360 and 360 for user input, be tolerant of floating-point fudge.
                bool spinX = Mathf.Abs(eulerAngles.x) >= 359.9f;
                bool spinY = Mathf.Abs(eulerAngles.y) >= 359.9f;
                bool spinZ = Mathf.Abs(eulerAngles.z) >= 359.9f;
                isFullRotation = spinX || spinY || spinZ;

                if (isFullRotation)
                {
                    // We'll rotate by + or -360 as requested on each axis, from current
                    // This is DELTA, but not shortest-path: we want a real full spin as asked
                    spinDeltaEuler = new Vector3(
                        spinX ? Mathf.Sign(eulerAngles.x) * 360f : 0f,
                        spinY ? Mathf.Sign(eulerAngles.y) * 360f : 0f,
                        spinZ ? Mathf.Sign(eulerAngles.z) * 360f : 0f
                    );
                    spinTargetDegrees = Mathf.Abs(spinDeltaEuler.x) + Mathf.Abs(spinDeltaEuler.y) + Mathf.Abs(spinDeltaEuler.z);
                    // For time mode, we need to know t goes from 0 to 1 and always does the requested full angle
                    // For speed mode, incrementally apply axis-locked rotation every frame
                    targetRotation = startRotation * Quaternion.Euler(spinDeltaEuler);
                }
                else
                {
                    // Normal lerp/rotate towards the eulerAngles as target
                    targetRotation = Quaternion.Euler(eulerAngles);
                    deltaAxis = Vector3.zero;
                    deltaAngle = Quaternion.Angle(startRotation, targetRotation);
                }
            }
            else if (spinType == SpinType.DELTA)
            {
                // For delta rotations, calculate the target as current + delta
                targetRotation = startRotation * Quaternion.Euler(eulerDelta);
                Quaternion deltaQ = Quaternion.Euler(eulerDelta);
                deltaQ.ToAngleAxis(out deltaAngle, out deltaAxis);
                if (deltaAngle > 180f)
                {
                    deltaAngle = 360f - deltaAngle;
                    deltaAxis = -deltaAxis;
                }
            }
            else if (spinType == SpinType.LOOK_AT)
            {
                if (target == null)
                {
                    target = Camera.main.transform;
                }

                targetRotation = Quaternion.LookRotation(target.position - transform.position);

                if (freezeX)
                {
                    targetRotation.x = transform.rotation.x;
                }
                if (freezeY)
                {
                    targetRotation.y = transform.rotation.y;
                }
                if (freezeZ)
                {
                    targetRotation.z = transform.rotation.z;
                }
            }

            if (!delayNextEvent)
            {
                onEvent?.Invoke(null);
            }
        }

        public void Update()
        {
            if (!isEnabled)
            {
                return;
            }
            switch (spinType)
            {
                case SpinType.ROTATION:
                case SpinType.LOOK_AT:
                    if (isFullRotation)
                    {
                        if (spinDuration == SpinDuration.TIME)
                        {
                            float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(time, Mathf.Epsilon));
                            // Lerp the requested full spin around all axes as specified
                            transform.localRotation = startRotation * Quaternion.Euler(new Vector3(
                                spinDeltaEuler.x * t,
                                spinDeltaEuler.y * t,
                                spinDeltaEuler.z * t
                            ));
                            if (t >= 1f)
                            {
                                transform.localRotation = targetRotation;
                                isEnabled = false;
                                if (delayNextEvent)
                                {
                                    onEvent?.Invoke(null);
                                }
                            }
                        }
                        else // SpinDuration.SPEED
                        {
                            // We spin each axis in local space, accumulating up to requested full amounts
                            // Always exactly the full spin on each axis: no Quaternion "shortest path"
                            float step = speed * Time.deltaTime;
                            bool finished = true;
                            Vector3 currEuler = (transform.localRotation * Quaternion.Inverse(startRotation)).eulerAngles;
                            Vector3 progressEuler = new Vector3(
                                NormalizeAngle(currEuler.x),
                                NormalizeAngle(currEuler.y),
                                NormalizeAngle(currEuler.z)
                            );

                            Vector3 newAngles = progressEuler;
                            Vector3 remain = new Vector3(
                                Mathf.Abs(spinDeltaEuler.x) - Mathf.Abs(progressEuler.x),
                                Mathf.Abs(spinDeltaEuler.y) - Mathf.Abs(progressEuler.y),
                                Mathf.Abs(spinDeltaEuler.z) - Mathf.Abs(progressEuler.z)
                            );

                            Vector3 stepAngles = Vector3.zero;
                            for (int i = 0; i < 3; ++i)
                            {
                                if (Mathf.Abs(spinDeltaEuler[i]) > Mathf.Epsilon)
                                {
                                    float dir = Mathf.Sign(spinDeltaEuler[i]);
                                    float thisRemain = Mathf.Max(0f, remain[i]);
                                    float thisStep = Mathf.Min(step, thisRemain);
                                    stepAngles[i] = dir * thisStep;
                                    if (thisRemain > 0.01f)
                                        finished = false;
                                }
                            }

                            if (!finished)
                            {
                                // Incremental rotation: rotate in *local* axis order. This is not a perfect analog to Quaternion math,
                                // but it ensures the visual spin is exactly as requested.
                                transform.localRotation *= Quaternion.Euler(stepAngles);
                            }
                            else
                            {
                                // Snap to exact target
                                transform.localRotation = targetRotation;
                                isEnabled = false;
                                if (delayNextEvent)
                                {
                                    onEvent?.Invoke(null);
                                }
                            }
                        }
                    }
                    else if (spinDuration == SpinDuration.TIME)
                    {
                        float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(time, Mathf.Epsilon));
                        transform.localRotation = Quaternion.Slerp(startRotation, targetRotation, t);
                        if (t >= 1f)
                        {
                            transform.localRotation = targetRotation;
                            isEnabled = false;
                            if (delayNextEvent)
                            {
                                onEvent?.Invoke(null);
                            }
                        }
                    }
                    else // SpinDuration.SPEED, normal "rotate to target"
                    {
                        float step = speed * Time.deltaTime;
                        float angleLeft = Quaternion.Angle(transform.localRotation, targetRotation);
                        if (step >= angleLeft || angleLeft <= Mathf.Epsilon)
                        {
                            transform.localRotation = targetRotation;
                            isEnabled = false;
                            if (delayNextEvent)
                            {
                                onEvent?.Invoke(null);
                            }
                        }
                        else
                        {
                            transform.localRotation = Quaternion.RotateTowards(transform.localRotation, targetRotation, step);
                        }
                    }
                    break;
                case SpinType.DELTA:
                    if (spinDuration == SpinDuration.TIME)
                    {
                        float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(time, Mathf.Epsilon));
                        transform.localRotation = Quaternion.Slerp(startRotation, targetRotation, t);
                        if (t >= 1f)
                        {
                            transform.localRotation = targetRotation;
                            isEnabled = false;
                            if (delayNextEvent)
                            {
                                onEvent?.Invoke(null);
                            }
                        }
                    }
                    else // spinDuration == SpinDuration.SPEED
                    {
                        float step = speed * Time.deltaTime;
                        if (rotatedSoFar < deltaAngle)
                        {
                            float remaining = deltaAngle - rotatedSoFar;
                            float thisStep = Mathf.Min(step, remaining);
                            if (deltaAxis.sqrMagnitude < Mathf.Epsilon)
                            {
                                // No rotation to perform, snap to target
                                transform.localRotation = targetRotation;
                                isEnabled = false;
                                if (delayNextEvent)
                                {
                                    onEvent?.Invoke(null);
                                }
                            }
                            else
                            {
                                // Apply the incremental rotation
                                Quaternion increment = Quaternion.AngleAxis(thisStep, deltaAxis.normalized);
                                transform.localRotation = transform.localRotation * increment;
                                rotatedSoFar += thisStep;
                                if (rotatedSoFar + Mathf.Epsilon >= deltaAngle)
                                {
                                    transform.localRotation = targetRotation;
                                    isEnabled = false;
                                    if (delayNextEvent)
                                    {
                                        onEvent?.Invoke(null);
                                    }
                                }
                            }
                        }
                        else
                        {
                            isEnabled = false;
                            if (delayNextEvent)
                            {
                                onEvent?.Invoke(null);
                            }
                        }
                    }
                    break;
            }
        }

        // Helper: Converts angle in [0,360) to signed [-180,180]
        private float NormalizeAngle(float a)
        {
            a = Mathf.Repeat(a + 180f, 360f) - 180f;
            return a;
        }
    }

}
