namespace DreamPark
{
    using UnityEngine;

    /// <summary>
    /// Builds the downward-facing quad that DreamPark hands to Meta's
    /// EnvironmentDepthManager as an occlusion MASK mesh (see DepthMask).
    ///
    /// A mask mesh is not drawn. Its only job is to be registered in
    /// EnvironmentDepthManager.MaskMeshFilters, which suppresses environment-depth
    /// occlusion for the region it covers — so virtual content under the quad keeps
    /// drawing instead of being eaten by the depth of a real ceiling, doorframe,
    /// lamp or wall behind it. Hence: renderer disabled, reversed winding so the
    /// normals face down at the player, "Triggers" layer, no collider, and
    /// OptimizedAFIgnore so OptimizedAF never parks it.
    ///
    /// AttractionTemplate (via LevelTemplate) and PropTemplate both build one; this
    /// class is the single definition of what "one" means, so the two cannot drift.
    /// </summary>
    public static class DepthCeiling
    {
        /// <summary>
        /// EnvironmentDepthManager.MaskBias is GLOBAL — every DepthMask that runs
        /// writes it, so the last component to initialize wins for the whole park.
        /// That is survivable only because every ceiling we build passes this same
        /// constant. If a caller ever needs a different bias, the fix is to make
        /// DepthMask stop stamping a per-object value onto a global, not to pass a
        /// second number here.
        /// </summary>
        public const float DefaultMaskBias = 0.6f;

        /// <summary>Below this a quad is not worth registering as a mask mesh.</summary>
        public const float MinSizeMeters = 0.25f;

        /// <summary>
        /// Create a horizontal mask quad of <paramref name="width"/> x <paramref name="length"/>
        /// metres, parented to <paramref name="parent"/> at <paramref name="localPosition"/>.
        /// Returns null if the size is degenerate.
        ///
        /// The quad is sized in the PARENT's scale, like any child object. Callers that
        /// need world metres regardless of how the parent is scaled should follow this
        /// with NeutralizeScale.
        /// </summary>
        public static GameObject Create(Transform parent, string name, float width, float length, Vector3 localPosition)
        {
            if (!(width >= MinSizeMeters) || !(length >= MinSizeMeters))
                return null;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            // Triggers is a layer nothing raycasts for ground or renders — the mask
            // must never answer a GroundProbe or a calibration ray.
            int triggers = LayerMask.NameToLayer("Triggers");
            if (triggers >= 0)
                go.layer = triggers;

            go.AddComponent<OptimizedAFIgnore>();

            var mf = go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>().enabled = false;

            float halfW = width * 0.5f;
            float halfL = length * 0.5f;

            var mesh = new Mesh { name = name + "Mesh" };
            mesh.vertices = new Vector3[4]
            {
                new Vector3(-halfW, 0f, -halfL),
                new Vector3(-halfW, 0f,  halfL),
                new Vector3( halfW, 0f,  halfL),
                new Vector3( halfW, 0f, -halfL)
            };
            // Reversed winding: the quad faces DOWN, at the player underneath it.
            mesh.triangles = new int[6] { 0, 2, 1, 0, 3, 2 };
            mesh.uv = new Vector2[4]
            {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f)
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            mf.sharedMesh = mesh;

            // DepthMask re-collects the mesh filters in its own Start/OnEnable
            // (GetComponentsInChildren, which finds this one), so there is nothing to
            // hand it here — an explicit myMeshFilters.Add would be overwritten.
            var depthMask = go.AddComponent<DepthMask>();
            depthMask._someOffsetFloatValue = DefaultMaskBias;

            return go;
        }

        /// <summary>
        /// Cancel the parent's scale so a quad built in metres stays that size in the
        /// world. A parent that is both rotated and non-uniformly scaled shears its
        /// children and cannot be fully cancelled by a local scale — the quad ends up
        /// approximately right there, which is what the padding is for.
        /// </summary>
        public static void NeutralizeScale(Transform t)
        {
            if (t == null || t.parent == null) return;
            Vector3 s = t.parent.lossyScale;
            t.localScale = new Vector3(SafeInverse(s.x), SafeInverse(s.y), SafeInverse(s.z));
        }

        private static float SafeInverse(float v)
        {
            return (Mathf.Abs(v) < 0.0001f || float.IsNaN(v) || float.IsInfinity(v)) ? 1f : 1f / v;
        }

    }
}
