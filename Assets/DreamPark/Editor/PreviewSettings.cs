#if !DREAMPARKCORE
using System;
using UnityEngine;

namespace DreamPark
{
    // Per-prefab overrides for the preview PNG that the Content Uploader
    // grid displays. Everything here is a delta on top of the default
    // PrefabPreviewRenderer behaviour:
    //
    //   - azimuth / elevation: where the camera orbits around the prefab.
    //     The renderer's baked-in default is 45° azimuth, 30° elevation.
    //   - zoom: a multiplier on the auto-framed camera distance AFTER the
    //     two-pass silhouette fit. 1 = the renderer's default framing,
    //     >1 = closer (silhouette fills more of the frame), <1 = pulled
    //     back (more breathing room).
    //
    // A settings value equal to Default renders byte-identically to the
    // original, angle-locked renderer — this is what keeps prefabs that
    // were never touched in the Preview Editor from churning their PNGs.
    [Serializable]
    public struct PreviewSettings
    {
        public float azimuth;
        public float elevation;
        public float zoom;

        // When true, objects on the "Gizmo" layer are culled from the preview
        // camera so in-prefab helper/gizmo objects don't show up in the PNG.
        // Default false — helper objects are visible by default, exactly like
        // the original renderer.
        public bool hideGizmoLayer;

        public const float DefaultAzimuth = 45f;
        public const float DefaultElevation = 30f;
        public const float DefaultZoom = 1f;
        public const bool DefaultHideGizmoLayer = false;

        // The project layer that in-prefab helper/gizmo objects live on.
        public const string GizmoLayerName = "Gizmo";

        // Sensible authoring bounds for the editor sliders / drag handlers.
        public const float MinElevation = -89f;
        public const float MaxElevation = 89f;
        public const float MinZoom = 0.25f;
        public const float MaxZoom = 4f;

        public static PreviewSettings Default => new PreviewSettings
        {
            azimuth = DefaultAzimuth,
            elevation = DefaultElevation,
            zoom = DefaultZoom,
            hideGizmoLayer = DefaultHideGizmoLayer,
        };

        public bool IsDefault
        {
            get
            {
                var d = Default;
                return Mathf.Approximately(azimuth, d.azimuth)
                    && Mathf.Approximately(elevation, d.elevation)
                    && Mathf.Approximately(zoom, d.zoom)
                    && hideGizmoLayer == d.hideGizmoLayer;
            }
        }

        // Clamp/repair any out-of-range or uninitialised values. Applied on
        // every read from disk and before every render so a hand-edited or
        // legacy JSON can never feed the renderer a NaN distance or a zoom
        // of 0 (which would collapse the camera onto the subject).
        public PreviewSettings Sanitized()
        {
            var s = this;
            if (float.IsNaN(s.zoom) || float.IsInfinity(s.zoom) || s.zoom <= 0f)
                s.zoom = DefaultZoom;
            s.zoom = Mathf.Clamp(s.zoom, MinZoom, MaxZoom);

            if (float.IsNaN(s.elevation) || float.IsInfinity(s.elevation))
                s.elevation = DefaultElevation;
            s.elevation = Mathf.Clamp(s.elevation, MinElevation, MaxElevation);

            if (float.IsNaN(s.azimuth) || float.IsInfinity(s.azimuth))
                s.azimuth = DefaultAzimuth;
            s.azimuth = Mathf.Repeat(s.azimuth, 360f);

            return s;
        }
    }
}
#endif
