#if !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    // A frozen animation pose for one animated entity inside a prefab.
    //
    // "Entity" here means a single GameObject that owns an Animator or a
    // legacy Animation component — the object a clip's curve paths are
    // relative to, and therefore the only object a clip can legally be
    // sampled onto. A prefab can contain several (a carousel with four
    // animated horses, a boss with an animated turret), which is why this is
    // stored as a list keyed by hierarchy path rather than a single value.
    //
    // Clip identity is a GUID + local file id, not an asset path: animation
    // clips are almost always sub-assets of an FBX, so the GUID alone names
    // the model and the local id picks the clip inside it. clipName is kept
    // as a human-readable fallback for the case where a re-import renumbers
    // local ids — resolution falls back to matching by name before giving up.
    [Serializable]
    public struct AnimationPose
    {
        // Hierarchy path of the Animator/Animation owner, relative to the
        // prefab root. "" means the root object itself.
        public string targetPath;

        public string clipGuid;
        public long clipFileId;
        public string clipName;

        // Where on the clip to freeze, 0..1 across its length. Normalized
        // rather than absolute seconds so a re-exported clip of a different
        // length still lands on the same relative pose instead of silently
        // clamping to the end.
        public float normalizedTime;

        // A pose is only meaningful if it can name an asset. clipName alone
        // isn't enough to find a clip again, so a GUID-less entry is treated
        // as "no pose" everywhere rather than resolving to null at render
        // time and quietly producing a bind-pose PNG.
        public bool HasClip => !string.IsNullOrEmpty(clipGuid);

        public AnimationPose Sanitized()
        {
            var p = this;
            p.targetPath ??= string.Empty;
            p.clipGuid ??= string.Empty;
            p.clipName ??= string.Empty;
            if (float.IsNaN(p.normalizedTime) || float.IsInfinity(p.normalizedTime))
                p.normalizedTime = 0f;
            p.normalizedTime = Mathf.Clamp01(p.normalizedTime);
            return p;
        }
    }

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
    //   - animationPoses: which clip each animated entity is frozen on, and
    //     at what point in that clip. Empty = no sampling at all, i.e. the
    //     prefab renders in its authored bind pose exactly as before.
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

        // Frozen animation poses, at most one per animated entity (keyed by
        // targetPath). Null/empty is the default and means "don't sample
        // anything" — the renderer skips the whole animation path, so no
        // existing preview changes until a creator opts in.
        public List<AnimationPose> animationPoses;

        public const float DefaultAzimuth = 45f;
        public const float DefaultElevation = 30f;
        public const float DefaultZoom = 1f;
        public const bool DefaultHideGizmoLayer = false;

        // Where the one-click "pose everything" action drops the playhead.
        // Far enough in to clear the wind-up frames most loops start on,
        // early enough to still read as the clip's signature pose.
        public const float SuggestedNormalizedTime = 0.3f;

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
            animationPoses = null,
        };

        // True when nothing here would change the render away from the
        // historical angle-locked, bind-pose output. Pose entries that carry
        // no clip don't count — they're inert bookkeeping, not an override.
        public bool IsDefault
        {
            get
            {
                var d = Default;
                if (!Mathf.Approximately(azimuth, d.azimuth)) return false;
                if (!Mathf.Approximately(elevation, d.elevation)) return false;
                if (!Mathf.Approximately(zoom, d.zoom)) return false;
                if (hideGizmoLayer != d.hideGizmoLayer) return false;
                return !HasAnyPose;
            }
        }

        public bool HasAnyPose
        {
            get
            {
                if (animationPoses == null) return false;
                for (int i = 0; i < animationPoses.Count; i++)
                    if (animationPoses[i].HasClip) return true;
                return false;
            }
        }

        // ── Pose accessors ──────────────────────────────────────────────
        // PreviewSettings is a struct but animationPoses is a reference, so
        // two copies of a settings value share one list. Every path that
        // hands a settings value to someone else goes through Sanitized(),
        // which deep-copies — that's what keeps the Preview Editor's live
        // edits from reaching back into the metadata store's parsed model.

        public bool TryGetPose(string targetPath, out AnimationPose pose)
        {
            targetPath ??= string.Empty;
            if (animationPoses != null)
            {
                for (int i = 0; i < animationPoses.Count; i++)
                {
                    if (animationPoses[i].targetPath == targetPath)
                    {
                        pose = animationPoses[i];
                        return true;
                    }
                }
            }
            pose = default;
            return false;
        }

        public void SetPose(AnimationPose pose)
        {
            pose = pose.Sanitized();
            animationPoses ??= new List<AnimationPose>();
            for (int i = 0; i < animationPoses.Count; i++)
            {
                if (animationPoses[i].targetPath == pose.targetPath)
                {
                    animationPoses[i] = pose;
                    return;
                }
            }
            animationPoses.Add(pose);
        }

        public void ClearPose(string targetPath)
        {
            if (animationPoses == null) return;
            targetPath ??= string.Empty;
            animationPoses.RemoveAll(p => p.targetPath == targetPath);
            if (animationPoses.Count == 0) animationPoses = null;
        }

        public void ClearAllPoses() => animationPoses = null;

        // Clamp/repair any out-of-range or uninitialised values, and detach
        // the pose list from whatever copy we were handed. Applied on every
        // read from disk and before every render so a hand-edited or legacy
        // JSON can never feed the renderer a NaN distance or a zoom of 0
        // (which would collapse the camera onto the subject).
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

            // Deep-copy the pose list, dropping clipless entries and any
            // duplicate targetPath (last one wins — a hand-edited file
            // shouldn't be able to make one entity resolve two ways).
            if (s.animationPoses == null || s.animationPoses.Count == 0)
            {
                s.animationPoses = null;
                return s;
            }

            var copy = new List<AnimationPose>(s.animationPoses.Count);
            for (int i = 0; i < s.animationPoses.Count; i++)
            {
                var p = s.animationPoses[i].Sanitized();
                if (!p.HasClip) continue;

                bool replaced = false;
                for (int j = 0; j < copy.Count; j++)
                {
                    if (copy[j].targetPath == p.targetPath)
                    {
                        copy[j] = p;
                        replaced = true;
                        break;
                    }
                }
                if (!replaced) copy.Add(p);
            }

            s.animationPoses = copy.Count > 0 ? copy : null;
            return s;
        }
    }
}
#endif
