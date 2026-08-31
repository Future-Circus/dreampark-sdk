using UnityEngine;
#if DINOFRACTURE
using DinoFracture;
#endif

namespace DreamPark.Easy {
    /// <summary>
    /// Triggers DinoFracture-based shattering on the target object when fired.
    ///
    /// DinoFracture is an OPTIONAL dependency. The DreamPark SDK does not bundle
    /// DinoFracture (it is a separately licensed Unity Asset Store asset). To use
    /// EasyShatter, import DinoFracture into your project from the Asset Store.
    ///
    /// Without DinoFracture installed, this component compiles cleanly as a no-op
    /// stub — prefabs and scenes that reference it remain valid, and at runtime
    /// firing the event simply logs a warning instead of shattering. Once
    /// DinoFracture is imported, the DinoFractureDetector editor script
    /// (Assets/DreamPark/Editor/DinoFractureDetector.cs) automatically adds the
    /// DINOFRACTURE scripting define symbol and full functionality is restored.
    /// </summary>
    public class EasyShatter : EasyEvent
    {
        public GameObject fracturableObject;
        public GameObject fractureTemplate;
        public Material insideMaterial;
        // Stored as a plain int so the serialized layout is IDENTICAL on every
        // platform regardless of the DINOFRACTURE define. A serialized field
        // guarded by #if changes this MonoBehaviour's TypeTree per build target:
        // prefabs are authored with DINOFRACTURE defined (field present), but the
        // iOS build target lacked the define, so iOS baked a TypeTree WITHOUT
        // this field — every EasyShatter then deserialized off-by-4-bytes and
        // Unity rejected the whole bundle as corrupt ("Position out of bounds").
        // int is binary-compatible with the previously-serialized FractureType
        // enum, so existing prefab data still reads correctly. NEVER put a
        // serialized field behind #if. Maps to FractureType at runtime below.
        public int fractureType = 0; // 0 == FractureType.Shatter
        public int numFracturePieces = 3;
        public int numIterations = 1;
        public bool evenlySizedPieces = true;
        private bool shattered = false;

        public override void Awake() {
            base.Awake();
            if (fracturableObject == null) {
                fracturableObject = GetComponentInChildren<MeshFilter>()?.gameObject;
            }
        }
        public override void OnEvent(object arg0 = null)
        {
#if DINOFRACTURE
            if (!shattered) {
                fracturableObject.transform.SetParent(null,true);
                Componentizer.DoComponent<RuntimeFracturedGeometry>(fracturableObject, true);
                if (fracturableObject.TryGetComponent<FractureGeometry>(out var fractureGeometry)) {
                    fractureGeometry.FractureTemplate = fractureTemplate;
                    fractureGeometry.NumFracturePieces = numFracturePieces;
                    fractureGeometry.NumIterations = numIterations;
                    fractureGeometry.EvenlySizedPieces = evenlySizedPieces;
                    fractureGeometry.FractureType = (FractureType)fractureType;
                    if (insideMaterial) {
                        fractureGeometry.InsideMaterial = insideMaterial;
                    }
                    fractureGeometry.Fracture();
                }
                shattered = true;
            }
#else
            Debug.LogWarning(
                $"[EasyShatter] DinoFracture is not installed — shatter skipped on '{name}'. " +
                "Import DinoFracture from the Unity Asset Store to enable shattering.",
                this);
#endif
            onEvent?.Invoke(null);
        }
    }
}
