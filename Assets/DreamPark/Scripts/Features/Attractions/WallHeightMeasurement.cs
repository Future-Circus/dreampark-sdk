namespace DreamPark
{
    using UnityEngine;

    /// <summary>
    /// Scene-independent "how tall does a wall need to be to back this content"
    /// measurement, shared by LevelTemplate's and PropTemplate's wall gizmos and
    /// (for PropTemplate) the Content Uploader.
    ///
    /// Walks collider SHAPE data (serialized fields, via PropTemplate's own
    /// TryGetLocalShapeBounds) and Transform.TransformPoint — never
    /// Collider.bounds or Renderer.bounds. Both of those are WORLD-space AABBs
    /// that only mean anything for an INSTANTIATED object; see the
    /// PropTemplate.FootprintMeters docblock for the full reasoning, which
    /// applies identically here. Transform.position/rotation and a collider's
    /// own shape fields are plain serialized asset data and read correctly
    /// with no scene at all, which is what makes this safe to call from a
    /// prefab asset loaded via AssetDatabase.LoadAssetAtPath.
    /// </summary>
    public static class WallHeightMeasurement
    {
        public const float DefaultWallHeightMeters = 3.048f; // 10 ft

        /// <summary>
        /// floorWorldY is the caller's own notion of "ground" — LevelTemplate's
        /// floor sits at its root's world Y, while PropTemplate's floor is
        /// SurfaceHeight (position.y plus the calibrated Y offset) — so it is
        /// passed in rather than assumed here.
        /// </summary>
        public static float GetWallHeightMeters(Transform root, float floorWorldY)
        {
            float maxY = floorWorldY;
            var colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (collider == null || !collider.enabled) continue;
                if (!PropTemplate.TryGetLocalShapeBounds(collider, out Bounds shape)) continue;

                Vector3 c = shape.center;
                Vector3 e = shape.extents;
                Transform ct = collider.transform;

                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 worldCorner = ct.TransformPoint(
                                new Vector3(c.x + e.x * sx, c.y + e.y * sy, c.z + e.z * sz));
                            if (float.IsNaN(worldCorner.y)) continue;
                            if (worldCorner.y > maxY) maxY = worldCorner.y;
                        }
            }

            return Mathf.Max(DefaultWallHeightMeters, maxY - floorWorldY);
        }
    }
}
