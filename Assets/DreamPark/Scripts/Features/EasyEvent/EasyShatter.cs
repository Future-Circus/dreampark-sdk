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
#if DINOFRACTURE
        public FractureType fractureType = FractureType.Shatter;
#endif
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
                    fractureGeometry.FractureType = fractureType;
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
