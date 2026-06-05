namespace DreamPark.Easy
{
    using UnityEngine;

#if UNITY_EDITOR
    using UnityEditor;

    [CustomEditor(typeof(EasyDetect), true)]
    public class EasyDetectEditor : EasyEventEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
        }

        public void OnSceneGUI()
        {
            OnSceneGUI_EasyDetect();
        }

        public void OnSceneGUI_EasyDetect()
        {
            EasyDetect easy = (EasyDetect)target;
            Handles.color = Color.red;

            if (easy.shape == EasyDetect.DetectShape.Sphere)
            {
                Vector3 handlePos =
                    easy.transform.position + Vector3.forward * easy.detectionRange;

                easy.detectionRange = Handles.ScaleValueHandle(
                    easy.detectionRange,
                    handlePos,
                    Quaternion.identity,
                    1f,
                    Handles.CubeHandleCap,
                    0.1f
                );

                Handles.DrawWireDisc(easy.transform.position, Vector3.up, easy.detectionRange);
            }
            else if (easy.shape == EasyDetect.DetectShape.Box)
            {
                // WORLD position of box center
                Vector3 worldCenter =
                    easy.transform.TransformPoint(easy.boxCenter);

                // Move handle for center
                EditorGUI.BeginChangeCheck();
                Vector3 newCenter =
                    Handles.PositionHandle(worldCenter, easy.transform.rotation);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(easy, "Move Box Center");
                    easy.boxCenter = easy.transform.InverseTransformPoint(newCenter);
                }

                // Scale handle
                EditorGUI.BeginChangeCheck();
                Vector3 newSize = Handles.ScaleHandle(
                    easy.boxSize,
                    worldCenter,
                    easy.transform.rotation,
                    1f
                );
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(easy, "Resize Box");
                    easy.boxSize = newSize;
                }

                // Draw the box
                Handles.matrix = easy.transform.localToWorldMatrix;
                Handles.color = Color.red;
                Handles.DrawWireCube(easy.boxCenter, easy.boxSize);
                Handles.matrix = Matrix4x4.identity;
            } else if (easy.shape == EasyDetect.DetectShape.LevelTemplate) {
                Handles.color = Color.red;
                Handles.DrawWireCube(easy.boxCenter, easy.boxSize);
            }

            if (GUI.changed)
            {
                Undo.RecordObject(easy, "EasyDetect Change");
            }
        }
    }
#endif

    public class EasyDetect : EasyEvent
    {
        public enum DetectShape { Sphere, Box, LevelTemplate }

        public DetectShape shape = DetectShape.Sphere;

        // -----------------------
        // Sphere fields
        // -----------------------
        [ShowIf("shape", DetectShape.Sphere)] public float detectionRange = 1.8f;

        // -----------------------
        // Box fields (local)
        // -----------------------
        [ShowIf("shape", DetectShape.Box)] public Vector3 boxCenter = Vector3.zero;
        [ShowIf("shape", DetectShape.Box)] public Vector3 boxSize = new Vector3(2f, 2f, 2f);

        public bool detectPlayer = true;
        [HideIf("detectPlayer")] public bool detectTarget = false;
        [HideIf("detectPlayer")][ShowIf("detectTarget")] public Transform target;
        [HideIf("detectPlayer")] public bool detectTag = false;
        [HideIf("detectPlayer")][ShowIf("detectTag")] public string targetTag = "Player";
        public bool alwaysPursueAfterDetection = false;

        private bool hasTarget = false;
        private GameObject _target;
        private float cooldown = 0f;
        public GameObject RuntimeTarget => _target;
        public bool HasDetectedTarget => hasTarget;

        public override void OnEvent(object arg0 = null)
        {
            if (detectPlayer)
                _target = Camera.main.gameObject;
            else if (detectTarget && target != null)
                _target = target.gameObject;

            isEnabled = true;
        }

        public override void Start()
        {
            SyncLevelTemplateSettings();
            base.Start();
        }

        public override void OnValidate()
        {
            Debug.Log("OnValidate: " + shape);
            base.OnValidate();
            SyncLevelTemplateSettings();
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

            if (shape == DetectShape.Sphere) {
                if (detectTag && _target == null) {
                    //do a spherecast to find the target with tag
                    Collider[] targets = Physics.OverlapSphere(gameObject.transform.position, detectionRange);
                    foreach (Collider collider in targets) {
                        if (collider.gameObject.CompareTag(targetTag)) {
                            _target = target.gameObject;
                            break;
                        }
                    }
                    cooldown = 0.5f;
                }
                if (_target != null) {
                    var p1 = new Vector3(_target.transform.position.x,0,_target.transform.position.z);
                    var p2 = new Vector3(transform.position.x,0,transform.position.z);
                    if (p1.Distance(p2) < detectionRange || hasTarget && alwaysPursueAfterDetection) {
                        OnEventDisable();
                        hasTarget = true;
                        Debug.Log("[EasyDetect] " + gameObject.name + " : Target detected: " + _target.name);
                        onEvent?.Invoke(_target);
                    }
                }
            } else if (shape == DetectShape.Box) {
                if (detectTag && _target == null) {
                    Vector3 worldCenter = transform.TransformPoint(boxCenter);
                    Collider[] targets = Physics.OverlapBox(worldCenter, boxSize/2f, transform.rotation);
                    foreach (Collider collider in targets) {
                        if (collider.gameObject.CompareTag(targetTag)) {
                            _target = collider.gameObject;
                            break;
                        }
                    }
                    cooldown = 0.5f;
                }
                if (_target != null) {
                    if (IsPointWithinBounds(_target.transform.position, transform, boxCenter, boxSize/2f)) {
                        OnEventDisable();
                        hasTarget = true;
                        Debug.Log("[EasyDetect] " + gameObject.name + " : Target detected: " + _target.name);
                        onEvent?.Invoke(_target);
                    }
                }
            } else if (shape == DetectShape.LevelTemplate) {
                if (detectTag && _target == null) {
                    Collider[] targets = Physics.OverlapBox(boxCenter, boxSize/2f);
                    foreach (Collider collider in targets) {
                        if (collider.gameObject.CompareTag(targetTag)) {
                            _target = collider.gameObject;
                            break;
                        }
                    }
                    cooldown = 0.5f;
                }
                if (_target != null) {
                    if (IsPointWithinWorldBounds(_target.transform.position, boxCenter, boxSize/2f)) {
                        OnEventDisable();
                        hasTarget = true;
                        Debug.Log("[EasyDetect] " + gameObject.name + " : Target detected: " + _target.name);
                        onEvent?.Invoke(_target);
                    }
                }
            }
        }
        bool IsPointWithinBounds(Vector3 point, Transform obj, Vector3 localCenter, Vector3 halfExtents)
        {
            // Convert point into local space relative to center
            Vector3 localPoint =
                obj.InverseTransformPoint(point) - localCenter;

            return Mathf.Abs(localPoint.x) <= halfExtents.x &&
                   Mathf.Abs(localPoint.y) <= halfExtents.y &&
                   Mathf.Abs(localPoint.z) <= halfExtents.z;
        }

        bool IsPointWithinWorldBounds(Vector3 point, Vector3 worldCenter, Vector3 halfExtents)
        {
            Vector3 worldPoint = point - worldCenter;
            return Mathf.Abs(worldPoint.x) <= halfExtents.x &&
                   Mathf.Abs(worldPoint.y) <= halfExtents.y &&
                   Mathf.Abs(worldPoint.z) <= halfExtents.z;
        }

        private void SyncLevelTemplateSettings()
        {
            if (shape != DetectShape.LevelTemplate) {
                return;
            }

            Debug.Log("LevelTemplate detected");
            if (gameObject.TryGetComponentInParent(out LevelTemplate lt)) {
                Debug.Log("LevelTemplate found: " + lt.name);
                boxCenter = lt.transform.position + new Vector3(0, 1f, 0);
                boxSize = lt.Size + new Vector3(0, 2f, 0);
            }
        }
    }
}
