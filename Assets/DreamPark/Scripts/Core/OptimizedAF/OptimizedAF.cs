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
            // Fixed foveation, High (Nov 11 2026). Under OpenXR the project uses the SRP Foveation API (OpenXR
            // settings + Unity's Foveated Rendering feature, kept on by DreamParkQuestPerformanceSettings); its
            // strength is XRDisplaySubsystem.foveatedRenderingLevel, 0..1, which Quest maps to its levels (1 = High).
            // OVRManager.foveatedRenderingLevel is the Legacy (Meta) API and does nothing under SRP Foveation.
            var xrDisplays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(xrDisplays);
            foreach (var xrDisplay in xrDisplays)
                xrDisplay.foveatedRenderingLevel = 1f;
            Debug.Log($"[OptimizedAF] foveation level 1 (High) on {xrDisplays.Count} XR display(s), caps {SystemInfo.foveatedRenderingCaps}");
            // Physics stays at ProjectSettings' 0.02 s (50 Hz): the director, Nov 11 2026, "50 physics steps was working
            // fine, the frame rate low is even worse for physics and interaction" (the Nov 2 1/90 s override is gone).
            // Catch-up after a hitch is capped at 0.05 s so one slow frame can't run a long chain of physics steps
            // and drop the next frame too.
            Time.maximumDeltaTime = 0.05f;
            // Incremental GC gets ~1 ms of each 11.1 ms frame (Nov 2 2026, flags-perf-nov2): under OpenXR Unity can't
            // see the frame's spare time, so set the slice explicitly instead of its larger default.
            UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds = 1_000_000UL;
        #endif

            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 0;

            ApplyFpsCounter();
        #if DREAMPARKCORE
            HeadsetFlags.Changed += ApplyFpsCounter;
        #endif
        }

        void OnDestroy()
        {
        #if DREAMPARKCORE
            HeadsetFlags.Changed -= ApplyFpsCounter;
        #endif
        }

        // The head-locked FPS readout: the inspector's showFPS, or (core) the park flag `fps_counter` (Nov 2 2026).
        void ApplyFpsCounter()
        {
            bool want = showFPS;
        #if DREAMPARKCORE
            want |= HeadsetFlags.Has(HeadsetFlags.FpsCounter);
        #endif
            var display = GetComponent<FPSDisplay>();
            if (want && display == null) gameObject.AddComponent<FPSDisplay>();
            else if (!want && display != null) Destroy(display);
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
            // frameInterval is in 50 Hz steps (the sweep's period, 0.6 s): keep that period at any fixed rate.
            if (++frameSkip < Mathf.Max(1, Mathf.RoundToInt(settings.frameInterval * 0.02f / Time.fixedDeltaTime))) return;
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
