#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools
{
    /// <summary>
    /// Uniform-scale fitting for imported props and characters.
    ///
    /// Why this exists: imported prefabs ship at whatever scale the vendor authored.
    /// Hand-tuning per-axis scale (Vector3) warps models — a sword stretched to a
    /// "0.1 × 1.0 × 0.1" target ends up ten times longer than its hilt is wide,
    /// because the source bounds weren't 1:1 to begin with. The fix is to fit one
    /// canonical axis (length for weapons, height for characters, diameter for coins)
    /// and let the others fall out proportionally.
    ///
    /// This class only exposes scalar fitting. There is no per-axis scaler. If you
    /// need per-axis scaling, your model is wrong, not your scale.
    ///
    /// Usage:
    ///   PrefabScaler.ScaleToFit(visual, 0.9f, Axis.Y);     // 0.9m tall along Y
    ///   PrefabScaler.ScaleToFit(visual, 0.05f, Axis.Z);    // 5cm along Z
    ///
    /// Returns: a ScaleResult with the factor applied and the source bounds, so
    /// the caller can serialize it into a results.json for QA.
    /// </summary>
    public static class PrefabScaler
    {
        public enum Axis { X, Y, Z }

        [Serializable]
        public struct ScaleResult
        {
            public bool ok;
            public float factor;          // the uniform scale multiplier we applied
            public Vector3 sourceSize;    // unscaled bounds.size of the source visual
            public float sourceAxisSize;  // sourceSize component along the chosen axis
            public float targetMeters;    // echo of the input target
            public Axis axis;             // echo of the input axis
            public string error;          // populated when ok=false

            public override string ToString()
            {
                if (!ok) return $"[PrefabScaler] FAILED: {error}";
                return $"[PrefabScaler] axis={axis} target={targetMeters}m " +
                       $"sourceAxis={sourceAxisSize:F4}m factor={factor:F4} sourceSize={sourceSize}";
            }
        }

        /// <summary>
        /// Apply a uniform scale to <paramref name="visual"/> so its renderer bounds along
        /// <paramref name="axis"/> measure exactly <paramref name="targetMeters"/>.
        /// All three local-scale components end up equal — the model cannot warp.
        ///
        /// The caller is responsible for choosing the visual GameObject (typically the
        /// 'Visual' grandchild under PropTemplate). The visual must have a Renderer
        /// (or Renderer in children) — otherwise we cannot measure source size and we
        /// fail loudly rather than guessing.
        /// </summary>
        public static ScaleResult ScaleToFit(GameObject visual, float targetMeters, Axis axis)
        {
            var result = new ScaleResult { axis = axis, targetMeters = targetMeters };

            if (visual == null)
            {
                result.error = "visual GameObject is null";
                Debug.LogError(result);
                return result;
            }
            if (targetMeters <= 0f)
            {
                result.error = $"targetMeters must be > 0, got {targetMeters}";
                Debug.LogError(result);
                return result;
            }

            // Reset to identity FIRST so we measure the source bounds, not whatever the
            // previous run left on the transform. Then we apply the new factor.
            // (Without this, re-running ScaleToFit on an already-scaled prefab would
            // double-apply the scale.)
            visual.transform.localScale = Vector3.one;

            // Force any pending TRS changes to flush into the renderer's world bounds.
            // Without this, freshly-instantiated prefabs report stale bounds.
            Physics.SyncTransforms();

            var renderer = visual.GetComponentInChildren<Renderer>();
            if (renderer == null)
            {
                result.error = $"'{visual.name}' has no Renderer in children — cannot measure source size";
                Debug.LogError(result);
                return result;
            }

            // Use the renderer.localBounds where available — bounds is world-space and
            // depends on parent transforms, which we don't want. localBounds is in the
            // mesh's own space (pre-transform), exactly what we need to compute a clean
            // local-scale factor.
            var localBounds = TryGetLocalBounds(renderer);
            result.sourceSize = localBounds.size;

            float current;
            switch (axis)
            {
                case Axis.X: current = localBounds.size.x; break;
                case Axis.Y: current = localBounds.size.y; break;
                default:     current = localBounds.size.z; break;
            }
            result.sourceAxisSize = current;

            if (current <= Mathf.Epsilon)
            {
                result.error = $"source size on axis {axis} is ~0 ({current}) — cannot fit";
                Debug.LogError(result);
                return result;
            }

            float factor = targetMeters / current;
            visual.transform.localScale = new Vector3(factor, factor, factor);
            result.factor = factor;
            result.ok = true;

            // Mark dirty so the change persists when called from an Editor script
            // operating on a prefab asset rather than a scene instance.
            EditorUtility.SetDirty(visual);

            Debug.Log(result);
            return result;
        }

        /// <summary>
        /// Convenience overload that resolves a prefab asset by path, opens it for
        /// editing, scales the named child, saves. For agent use via execute_csharp.
        /// </summary>
        public static ScaleResult ScaleToFitInPrefab(string prefabAssetPath, string visualChildName,
                                                     float targetMeters, Axis axis)
        {
            var result = new ScaleResult { axis = axis, targetMeters = targetMeters };

            var contents = PrefabUtility.LoadPrefabContents(prefabAssetPath);
            if (contents == null)
            {
                result.error = $"could not load prefab at '{prefabAssetPath}'";
                Debug.LogError(result);
                return result;
            }
            try
            {
                Transform target = string.IsNullOrEmpty(visualChildName)
                    ? contents.transform
                    : contents.transform.Find(visualChildName);
                if (target == null)
                {
                    result.error = $"no child named '{visualChildName}' under prefab '{prefabAssetPath}'";
                    Debug.LogError(result);
                    return result;
                }
                result = ScaleToFit(target.gameObject, targetMeters, axis);
                if (result.ok)
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, prefabAssetPath);
                }
                return result;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // ─── Internals ──────────────────────────────────────────────────────────

        static Bounds TryGetLocalBounds(Renderer r)
        {
            // MeshRenderer + SkinnedMeshRenderer both expose localBounds, but only
            // SkinnedMeshRenderer has it as a public property. For MeshRenderer we
            // fall back to MeshFilter.sharedMesh.bounds, which is the same thing.
            if (r is SkinnedMeshRenderer smr)
            {
                return smr.localBounds;
            }
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                return mf.sharedMesh.bounds;
            }
            // Last resort: world bounds. Less accurate if the GO has parent transforms,
            // but the caller has already reset localScale to identity so it's reasonable.
            return r.bounds;
        }
    }
}
#endif
