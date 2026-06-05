namespace DreamPark.Easy
{
    using UnityEngine;

#if UNITY_EDITOR
    using UnityEditor;

    [CustomEditor(typeof(EasyUndetect), true)]
    public class EasyUndetectEditor : EasyEventEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
        }

        public void OnSceneGUI()
        {
            OnSceneGUI_EasyUndetect();
        }

        public void OnSceneGUI_EasyUndetect()
        {
            EasyUndetect easy = (EasyUndetect)target;
            Handles.color = Color.cyan;

            if (easy.shape == EasyUndetect.DetectShape.Sphere)
            {
                Vector3 handlePos = easy.transform.position + Vector3.forward * easy.detectionRange;

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
            else if (easy.shape == EasyUndetect.DetectShape.Box)
            {
                Vector3 worldCenter = easy.transform.TransformPoint(easy.boxCenter);

                EditorGUI.BeginChangeCheck();
                Vector3 newCenter = Handles.PositionHandle(worldCenter, easy.transform.rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(easy, "Move Box Center");
                    easy.boxCenter = easy.transform.InverseTransformPoint(newCenter);
                }

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

                Handles.matrix = easy.transform.localToWorldMatrix;
                Handles.color = Color.cyan;
                Handles.DrawWireCube(easy.boxCenter, easy.boxSize);
                Handles.matrix = Matrix4x4.identity;
            }
            else if (easy.shape == EasyUndetect.DetectShape.LevelTemplate)
            {
                Handles.color = Color.cyan;
                Handles.DrawWireCube(easy.boxCenter, easy.boxSize);
            }

            if (GUI.changed)
            {
                Undo.RecordObject(easy, "EasyUndetect Change");
            }
        }
    }
#endif

    public class EasyUndetect : EasyEvent
    {
        public enum DetectShape { Sphere, Box, LevelTemplate }

        public DetectShape shape = DetectShape.Sphere;

        [ShowIf("shape", DetectShape.Sphere)] public float detectionRange = 1.8f;
        [HideIf("hideBoxSettings")] public Vector3 boxCenter = Vector3.zero;
        [HideIf("hideBoxSettings")] public Vector3 boxSize = new Vector3(2f, 2f, 2f);

        public bool autoLinkEasyDetectTarget = false;
        [ReadOnly] public bool hasEasyDetectOnObject = false;
        [ShowIf("autoLinkEasyDetectTarget")] [ReadOnly] public string easyDetectTargetStatus = "Waiting on EasyDetect target..";
        [ShowIf("autoLinkEasyDetectTarget")] [ReadOnly] public GameObject runtimeEasyDetectTarget;

        [HideIf("hideDetectPlayer")] public bool detectPlayer = true;
        [HideIf("hideDetectTarget")] public bool detectTarget = false;
        [HideIf("hideTarget")] public Transform target;
        [HideIf("hideDetectTag")] public bool detectTag = false;
        [HideIf("hideTargetTag")] public string targetTag = "Player";

        private GameObject _target;
        private EasyDetect _easyDetect;
        private bool _hasBeenDetected;
        private float _cooldown;
        [SerializeField, HideInInspector] private bool _defaultsInitialized;
        [HideInInspector] public bool hideBoxSettings;
        [HideInInspector] public bool hideDetectPlayer;
        [HideInInspector] public bool hideDetectTarget;
        [HideInInspector] public bool hideTarget;
        [HideInInspector] public bool hideDetectTag;
        [HideInInspector] public bool hideTargetTag;

        public override void Start()
        {
            SyncLevelTemplateSettings();
            ResolveEasyDetectReference();
            RegisterEasyDetectListener();
            base.Start();
        }

        private void OnDestroy()
        {
            UnregisterEasyDetectListener();
        }

        public override void OnEvent(object arg0 = null)
        {
            ResolveEasyDetectReference();
            _hasBeenDetected = false;
            _cooldown = 0f;

            if (autoLinkEasyDetectTarget)
            {
                RegisterEasyDetectListener();
                if (!TryAssignTargetFromObject(arg0))
                    TryResolveTargetFromEasyDetect();
            }
            else
            {
                if (!TryAssignTargetFromObject(arg0))
                {
                    if (detectPlayer && Camera.main != null)
                        _target = Camera.main.gameObject;
                    else if (detectTarget && target != null)
                        _target = target.gameObject;
                }
            }

            if (_target != null && autoLinkEasyDetectTarget)
                _hasBeenDetected = true;

            UpdateEasyDetectStatus();
            isEnabled = true;
        }

        public void Update()
        {
            if (!isEnabled)
                return;

            if (_cooldown > 0f)
            {
                _cooldown -= Time.deltaTime;
                return;
            }

            if (autoLinkEasyDetectTarget)
                TryResolveTargetFromEasyDetect();

            if (_target == null && detectTag && !autoLinkEasyDetectTarget)
            {
                if (shape == DetectShape.Sphere)
                {
                    Collider[] targets = Physics.OverlapSphere(transform.position, detectionRange);
                    foreach (Collider collider in targets)
                    {
                        if (collider.gameObject.CompareTag(targetTag))
                        {
                            _target = collider.gameObject;
                            break;
                        }
                    }
                }
                else if (shape == DetectShape.Box)
                {
                    Vector3 worldCenter = transform.TransformPoint(boxCenter);
                    Collider[] targets = Physics.OverlapBox(worldCenter, boxSize / 2f, transform.rotation);
                    foreach (Collider collider in targets)
                    {
                        if (collider.gameObject.CompareTag(targetTag))
                        {
                            _target = collider.gameObject;
                            break;
                        }
                    }
                }
                else if (shape == DetectShape.LevelTemplate)
                {
                    Collider[] targets = Physics.OverlapBox(boxCenter, boxSize / 2f);
                    foreach (Collider collider in targets)
                    {
                        if (collider.gameObject.CompareTag(targetTag))
                        {
                            _target = collider.gameObject;
                            break;
                        }
                    }
                }

                _cooldown = 0.5f;
            }

            UpdateEasyDetectStatus();

            if (_target == null)
                return;

            bool currentlyDetected = IsTargetWithinDetection(_target.transform.position);
            if (currentlyDetected)
            {
                _hasBeenDetected = true;
                return;
            }

            if (!_hasBeenDetected)
                return;

            OnEventDisable();
            Debug.Log("[EasyUndetect] " + gameObject.name + " : Target undetected: " + _target.name);
            onEvent?.Invoke(_target);
        }

        public override void OnValidate()
        {
            base.OnValidate();
            SyncLevelTemplateSettings();
            ResolveEasyDetectReference();
            SyncEditorDefaults();
            SyncInspectorVisibility();
            UpdateEasyDetectStatus();
        }

        private void ResolveEasyDetectReference()
        {
            if (!TryGetComponent(out _easyDetect))
                _easyDetect = null;

            hasEasyDetectOnObject = _easyDetect != null;
            if (!hasEasyDetectOnObject)
                runtimeEasyDetectTarget = null;
        }

        private void RegisterEasyDetectListener()
        {
            if (_easyDetect == null || !autoLinkEasyDetectTarget)
                return;

            _easyDetect.onEvent.RemoveListener(OnEasyDetectEvent);
            _easyDetect.onEvent.AddListener(OnEasyDetectEvent);
        }

        private void UnregisterEasyDetectListener()
        {
            if (_easyDetect != null)
                _easyDetect.onEvent.RemoveListener(OnEasyDetectEvent);
        }

        private void OnEasyDetectEvent(object arg0)
        {
            if (TryAssignTargetFromObject(arg0))
            {
                _hasBeenDetected = true;
                UpdateEasyDetectStatus();
                return;
            }

            TryResolveTargetFromEasyDetect();
            if (_target != null)
                _hasBeenDetected = true;
            UpdateEasyDetectStatus();
        }

        private void TryResolveTargetFromEasyDetect()
        {
            if (_easyDetect == null)
                return;

            if (_easyDetect.RuntimeTarget != null)
            {
                _target = _easyDetect.RuntimeTarget;
                runtimeEasyDetectTarget = _target;
            }
        }

        private bool TryAssignTargetFromObject(object arg0)
        {
            if (arg0 is GameObject go)
            {
                _target = go;
                runtimeEasyDetectTarget = go;
                return true;
            }

            if (arg0 is Component component)
            {
                _target = component.gameObject;
                runtimeEasyDetectTarget = _target;
                return true;
            }

            return false;
        }

        private bool IsTargetWithinDetection(Vector3 targetPosition)
        {
            if (shape == DetectShape.Sphere)
            {
                Vector3 targetFlat = new Vector3(targetPosition.x, 0f, targetPosition.z);
                Vector3 selfFlat = new Vector3(transform.position.x, 0f, transform.position.z);
                return Vector3.Distance(targetFlat, selfFlat) < detectionRange;
            }
            if (shape == DetectShape.LevelTemplate)
                return IsPointWithinWorldBounds(targetPosition, boxCenter, boxSize / 2f);

            return IsPointWithinBounds(targetPosition, transform, boxCenter, boxSize / 2f);
        }

        private static bool IsPointWithinBounds(Vector3 point, Transform obj, Vector3 localCenter, Vector3 halfExtents)
        {
            Vector3 localPoint = obj.InverseTransformPoint(point) - localCenter;
            return Mathf.Abs(localPoint.x) <= halfExtents.x &&
                   Mathf.Abs(localPoint.y) <= halfExtents.y &&
                   Mathf.Abs(localPoint.z) <= halfExtents.z;
        }

        private static bool IsPointWithinWorldBounds(Vector3 point, Vector3 worldCenter, Vector3 halfExtents)
        {
            Vector3 worldPoint = point - worldCenter;
            return Mathf.Abs(worldPoint.x) <= halfExtents.x &&
                   Mathf.Abs(worldPoint.y) <= halfExtents.y &&
                   Mathf.Abs(worldPoint.z) <= halfExtents.z;
        }

        private void SyncEditorDefaults()
        {
            if (_defaultsInitialized)
            {
                if (!hasEasyDetectOnObject && autoLinkEasyDetectTarget)
                    autoLinkEasyDetectTarget = false;
                return;
            }

            autoLinkEasyDetectTarget = hasEasyDetectOnObject;
            detectPlayer = !hasEasyDetectOnObject;
            _defaultsInitialized = true;
        }

        private void SyncInspectorVisibility()
        {
            bool manualMode = !autoLinkEasyDetectTarget;
            bool targetSettingsVisible = manualMode && !detectPlayer;

            hideBoxSettings = shape == DetectShape.Sphere;
            hideDetectPlayer = !manualMode;
            hideDetectTarget = !targetSettingsVisible;
            hideTarget = !(targetSettingsVisible && detectTarget);
            hideDetectTag = !targetSettingsVisible;
            hideTargetTag = !(targetSettingsVisible && detectTag);
        }

        private void SyncLevelTemplateSettings()
        {
            if (shape != DetectShape.LevelTemplate)
                return;

            if (gameObject.TryGetComponentInParent(out LevelTemplate lt))
            {
                boxCenter = lt.transform.position + new Vector3(0, 1f, 0);
                boxSize = lt.Size + new Vector3(0, 2f, 0);
            }
        }

        private void UpdateEasyDetectStatus()
        {
            if (!autoLinkEasyDetectTarget)
            {
                easyDetectTargetStatus = string.Empty;
                return;
            }

            if (!hasEasyDetectOnObject)
            {
                easyDetectTargetStatus = "EasyDetect missing on this object.";
                return;
            }

            if (runtimeEasyDetectTarget == null)
            {
                easyDetectTargetStatus = "Waiting on EasyDetect target..";
                return;
            }

            easyDetectTargetStatus = "Linked to " + runtimeEasyDetectTarget.name;
        }
    }
}
