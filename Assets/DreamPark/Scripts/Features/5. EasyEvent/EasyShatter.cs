using UnityEngine;
using DinoFracture;

namespace DreamPark.Easy {
    public class EasyShatter : EasyEvent
    {
        public GameObject fracturableObject;
        public GameObject fractureTemplate;
        public Material insideMaterial;
        public FractureType fractureType = FractureType.Shatter;
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
            onEvent?.Invoke(null);
        }
    }
}