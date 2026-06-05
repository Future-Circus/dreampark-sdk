namespace DreamPark.Easy
{
    using System;
    using UnityEngine;

    public class EasyMove : EasyEvent
    {
        public enum MoveType
        {
            POSITION,   // Move TO a specific position
            DIRECTION,  // Move in a direction for a specific vector length
            TARGET,     // Move to a target object
            GET_TARGET,
            OG_POSITION, // Move back to original position
            FLOOR       // Raycast down to find ground and position on top
        }
        public enum MoveDuration
        {
            TIME,       // Interpolate over a specific duration
            SPEED,      // Move at a constant speed
        }
        public enum MovementStyle
        {
            Linear,     // Straight line movement
            Lob,        // Arc movement with height
        }
        public MoveType moveType = MoveType.POSITION;
        public MovementStyle movementStyle = MovementStyle.Linear;
        [ShowIf("movementStyle", MovementStyle.Lob)] public float lobHeight = 2f;
        public MoveDuration moveDuration = MoveDuration.TIME;
        [ShowIf("moveType", MoveType.POSITION)] public Vector3 position;
        [ShowIf("moveType", MoveType.DIRECTION)] public Vector3 moveDirection; // Treated as displacement, not just direction
        [ShowIf("moveType", MoveType.DIRECTION)] public bool randomizeDirection = false;
        [ShowIf("randomizeDirection")] public Vector3 randomDirectionVariation;
        [ShowIf("moveDuration", MoveDuration.SPEED)] public float speed = 1f;
        [ShowIf("moveDuration", MoveDuration.TIME)] public float time = 1f;
        [ShowIf("moveType", MoveType.TARGET)] public Transform target;
        [ShowIf("moveType", MoveType.GET_TARGET)] public Func<Transform> getTarget;
        [ShowIf("moveType", MoveType.FLOOR)] public float raycastStartHeight = 1f;
        [ShowIf("moveType", MoveType.FLOOR)] public float raycastDistance = 20f;
        [ShowIf("moveType", MoveType.FLOOR)] public float groundOffset = 0f; // Additional offset above the ground
        private float startTime = 0f;
        private Vector3 startPosition;
        private Vector3 targetPosition;
        private Vector3 movementDirection;
        private float moveDistanceTotal = 0f;
        private float movedSoFar = 0f;
        public bool delayNextEvent = true;
        private Vector3 ogPosition;

        public override void Awake()
        {
            base.Awake();
            ogPosition = transform.localPosition;
        }

        private void CompleteMove(bool invokeNextEvent)
        {
            OnEventDisable();
            if (invokeNextEvent)
            {
                onEvent?.Invoke(null);
            }
        }

        public override void OnEvent(object arg0 = null)
        {
            isEnabled = true;
            startTime = Time.time;
            movedSoFar = 0f;

            // Use world position for TARGET and FLOOR modes, local position for others
            bool useWorldSpace = (moveType == MoveType.TARGET || moveType == MoveType.GET_TARGET || moveType == MoveType.FLOOR);
            startPosition = useWorldSpace ? transform.position : transform.localPosition;

            // Check if arg0 is a GameObject or Transform and use it as target (only for TARGET mode)
            if (moveType == MoveType.TARGET && target == null)
            {
                if (arg0 is GameObject go)
                {
                    target = go.transform;
                }
                else if (arg0 is Transform t)
                {
                    target = t;
                }
            }
            else if (moveType == MoveType.GET_TARGET)
            {
                target = getTarget?.Invoke();
            }

            if (moveType == MoveType.OG_POSITION)
            {
                position = ogPosition;
            }

            if (moveType == MoveType.POSITION || moveType == MoveType.OG_POSITION)
            {
                targetPosition = position;
                moveDistanceTotal = Vector3.Distance(startPosition, position);
                movementDirection = (position - startPosition).normalized;
            }
            else if (moveType == MoveType.DIRECTION)
            {
                Vector3 actualDirection = moveDirection;
                if (randomizeDirection)
                {
                    actualDirection = new Vector3(
                        UnityEngine.Random.Range(moveDirection.x - randomDirectionVariation.x, moveDirection.x + randomDirectionVariation.x),
                        UnityEngine.Random.Range(moveDirection.y - randomDirectionVariation.y, moveDirection.y + randomDirectionVariation.y),
                        UnityEngine.Random.Range(moveDirection.z - randomDirectionVariation.z, moveDirection.z + randomDirectionVariation.z)
                    );
                }
                targetPosition = startPosition + actualDirection;
                moveDistanceTotal = actualDirection.magnitude;
                movementDirection = actualDirection.normalized;
            }
            else if (moveType == MoveType.TARGET || moveType == MoveType.GET_TARGET)
            {
                if (target != null)
                {
                    targetPosition = target.position;
                    moveDistanceTotal = Vector3.Distance(startPosition, targetPosition);
                }
            }
            else if (moveType == MoveType.FLOOR)
            {
                // Raycast down from above the object's current XZ position to find ground
                int levelLayer = LayerMask.GetMask("Level");
                Vector3 rayOrigin = new Vector3(transform.position.x, transform.position.y + raycastStartHeight, transform.position.z);
                Ray ray = new Ray(rayOrigin, Vector3.down);

                if (Physics.Raycast(ray, out RaycastHit hit, raycastDistance, levelLayer))
                {
                    // Only accept hits on objects tagged "Ground"
                    if (hit.collider.CompareTag("Ground"))
                    {
                        targetPosition = hit.point + Vector3.up * groundOffset;
                        moveDistanceTotal = Vector3.Distance(startPosition, targetPosition);
                        movementDirection = (targetPosition - startPosition).normalized;
                    }
                    else
                    {
                        // Hit something on Level layer but not tagged Ground - stay in place
                        targetPosition = startPosition;
                        moveDistanceTotal = 0f;
                    }
                }
                else
                {
                    // No ground found - stay in place
                    targetPosition = startPosition;
                    moveDistanceTotal = 0f;
                }
            }

            // Handle instant movement (time <= 0)
            if (moveDuration == MoveDuration.TIME && time <= 0f)
            {
                ApplyInstantMove();
                CompleteMove(true);
                return;
            }

            if (!delayNextEvent)
            {
                onEvent?.Invoke(null);
            }
        }

        /// <summary>
        /// Returns the vertical offset for lob movement based on progress t (0 to 1).
        /// Uses a parabola: 4 * h * t * (1 - t) where h is lobHeight.
        /// </summary>
        private float GetLobOffset(float t)
        {
            return 4f * lobHeight * t * (1f - t);
        }

        private void ApplyInstantMove()
        {
            switch (moveType)
            {
                case MoveType.POSITION:
                case MoveType.OG_POSITION:
                    transform.localPosition = position;
                    break;
                case MoveType.DIRECTION:
                    transform.localPosition += moveDirection;
                    break;
                case MoveType.TARGET:
                case MoveType.GET_TARGET:
                    if (target != null)
                        transform.position = target.position;
                    break;
                case MoveType.FLOOR:
                    transform.position = targetPosition;
                    break;
            }
        }

        public void Update()
        {
            if (!isEnabled)
            {
                return;
            }
            switch (moveType)
            {
                case MoveType.POSITION:
                case MoveType.OG_POSITION:
                    if (moveDuration == MoveDuration.TIME)
                    {
                        float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(time, Mathf.Epsilon));
                        Vector3 basePos = Vector3.Lerp(startPosition, position, t);
                        if (movementStyle == MovementStyle.Lob)
                            basePos.y += GetLobOffset(t);
                        transform.localPosition = basePos;
                        if (t >= 1f)
                        {
                            transform.localPosition = position;
                            CompleteMove(delayNextEvent);
                        }
                    }
                    else // moveDuration == MoveDuration.SPEED
                    {
                        float step = speed * Time.deltaTime;
                        movedSoFar += step;
                        if (movedSoFar >= moveDistanceTotal)
                        {
                            transform.localPosition = position;
                            CompleteMove(delayNextEvent);
                        }
                        else
                        {
                            float t = movedSoFar / moveDistanceTotal;
                            Vector3 basePos = Vector3.Lerp(startPosition, position, t);
                            if (movementStyle == MovementStyle.Lob)
                                basePos.y += GetLobOffset(t);
                            transform.localPosition = basePos;
                        }
                    }
                    break;
                case MoveType.DIRECTION:
                    if (moveDuration == MoveDuration.TIME)
                    {
                        float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(time, Mathf.Epsilon));
                        Vector3 basePos = Vector3.Lerp(startPosition, targetPosition, t);
                        if (movementStyle == MovementStyle.Lob)
                            basePos.y += GetLobOffset(t);
                        transform.localPosition = basePos;
                        if (t >= 1f)
                        {
                            transform.localPosition = targetPosition;
                            CompleteMove(delayNextEvent);
                        }
                    }
                    else // moveDuration == MoveDuration.SPEED
                    {
                        float step = speed * Time.deltaTime;
                        movedSoFar += step;
                        if (movedSoFar >= moveDistanceTotal)
                        {
                            transform.localPosition = targetPosition;
                            CompleteMove(delayNextEvent);
                        }
                        else
                        {
                            float t = movedSoFar / moveDistanceTotal;
                            Vector3 basePos = Vector3.Lerp(startPosition, targetPosition, t);
                            if (movementStyle == MovementStyle.Lob)
                                basePos.y += GetLobOffset(t);
                            transform.localPosition = basePos;
                        }
                    }
                    break;
                case MoveType.TARGET or MoveType.GET_TARGET:
                    if (moveDuration == MoveDuration.TIME)
                    {
                        float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(time, Mathf.Epsilon));
                        Vector3 goal = target != null ? target.position : startPosition;
                        Vector3 basePos = Vector3.Lerp(startPosition, goal, t);
                        if (movementStyle == MovementStyle.Lob)
                            basePos.y += GetLobOffset(t);
                        transform.position = basePos;
                        if (t >= 1f)
                        {
                            transform.position = goal;
                            CompleteMove(delayNextEvent);
                        }
                    }
                    else // moveDuration == MoveDuration.SPEED
                    {
                        if (target == null)
                        {
                            OnEventDisable();
                            break;
                        }
                        float step = speed * Time.deltaTime;
                        movedSoFar += step;
                        if (movedSoFar >= moveDistanceTotal)
                        {
                            transform.position = targetPosition;
                            CompleteMove(delayNextEvent);
                        }
                        else
                        {
                            float t = movedSoFar / moveDistanceTotal;
                            Vector3 basePos = Vector3.Lerp(startPosition, targetPosition, t);
                            if (movementStyle == MovementStyle.Lob)
                                basePos.y += GetLobOffset(t);
                            transform.position = basePos;
                        }
                    }
                    break;
                case MoveType.FLOOR:
                    if (moveDuration == MoveDuration.TIME)
                    {
                        float t = Mathf.Clamp01((Time.time - startTime) / Mathf.Max(time, Mathf.Epsilon));
                        Vector3 basePos = Vector3.Lerp(startPosition, targetPosition, t);
                        if (movementStyle == MovementStyle.Lob)
                            basePos.y += GetLobOffset(t);
                        transform.position = basePos;
                        if (t >= 1f)
                        {
                            transform.position = targetPosition;
                            CompleteMove(delayNextEvent);
                        }
                    }
                    else // moveDuration == MoveDuration.SPEED
                    {
                        float step = speed * Time.deltaTime;
                        movedSoFar += step;
                        if (movedSoFar >= moveDistanceTotal)
                        {
                            transform.position = targetPosition;
                            CompleteMove(delayNextEvent);
                        }
                        else
                        {
                            float t = movedSoFar / moveDistanceTotal;
                            Vector3 basePos = Vector3.Lerp(startPosition, targetPosition, t);
                            if (movementStyle == MovementStyle.Lob)
                                basePos.y += GetLobOffset(t);
                            transform.position = basePos;
                        }
                    }
                    break;
            }
        }
    }

}
