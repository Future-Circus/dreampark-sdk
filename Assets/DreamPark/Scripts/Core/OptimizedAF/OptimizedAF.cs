using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DreamPark.ParkBuilder;
using UnityEngine.XR;

namespace DreamPark {
    public class OptimizedAF : MonoBehaviour
    {
        public OptimizationSettings settings;
        public bool showFPS = false;
        public bool showGizmos = false;
        public bool disable = false;
        private int frameSkip = 0;

        void Start()
        {
        #if !UNITY_EDITOR && UNITY_ANDROID
            OVRManager.instance.enableDynamicResolution = false;
            XRSettings.eyeTextureResolutionScale = settings.resolutionScaleFactor;

            OVRManager.display.displayFrequency = 90.0f;
            OVRPlugin.systemDisplayFrequency = 90.0f;
            OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.High;
        #endif

            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 0;

            if (showFPS)
                gameObject.AddComponent<FPSDisplay>();
        }

        public static void SignalLoadEvent()
        {
            #if !UNITY_EDITOR && UNITY_ANDROID
            OVRPlugin.suggestedCpuPerfLevel = OVRPlugin.ProcessorPerformanceLevel.SustainedHigh;
            OVRPlugin.suggestedGpuPerfLevel = OVRPlugin.ProcessorPerformanceLevel.SustainedHigh;
            #endif
        }

        public void RunOptimizedFrame()
        {
            if (disable) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            //Plane[] frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);

            int culledObjs = 0;
            
            var removedObjects = new List<LevelObject>();

            foreach (LevelObject obj in LevelObjectManager.Instance.levelObjects)
            {
                if (obj.transform == null) {
                    removedObjects.Add(obj);
                    continue;
                }

                if (obj.ignoreOptimization) {
                    continue;
                }

                Vector3 objPos = obj.transform.position;
                Vector3 camPosWithObjY = new Vector3(camPos.x, objPos.y, camPos.z);

                Vector3 closestPoint = obj.renderBounds.ClosestPoint(camPosWithObjY);

                float distance = Vector3.Distance(camPosWithObjY, closestPoint);
                // Quick distance check
                if (distance < settings.distanceBands[0])
                {
                    obj.Enable(settings);
                    continue;
                } else if (distance >= settings.distanceBands[0] && distance < settings.distanceBands[1]) {
                    obj.Disable(settings);
                } else if (distance >= settings.distanceBands[1] && distance < settings.distanceBands[2]) {
                    obj.DisableAndSimplifyRendering(settings);
                } else if (distance >= settings.distanceBands[2]) {
                    obj.DisableAndHide(settings);
                }
                culledObjs++;
            }

            foreach (LevelObject obj in removedObjects) {
                LevelObjectManager.Instance.levelObjects.Remove(obj);
            }

            if (showGizmos) {
                Debug.Log($"Culled {culledObjs} objects out of {LevelObjectManager.Instance.levelObjects.Count} and removed {removedObjects.Count} objects");
            }
        }

        void FixedUpdate()
        {
            #if DREAMPARKCORE
            if (NativeInterfaceManager.Instance?.buildMode == true) return;
            #endif
            if (++frameSkip < settings.frameInterval) return;
            frameSkip = 0;

            RunOptimizedFrame();
        }

        public void OnDrawGizmos()
        {
            if (!showGizmos) return;
            
            if (!Application.isPlaying) return;

            if (!gameObject.activeSelf) return;

            if (!enabled) return;
            
            Gizmos.color = Color.red;

            foreach (LevelObject obj in LevelObjectManager.Instance.levelObjects)
            {
                if (obj == null || obj.renderBounds == null || obj.gameObject == null || !obj.gameObject.activeSelf) continue;

                Gizmos.DrawWireCube(obj.renderBounds.center, obj.renderBounds.size);
            }
        }
    }
}
