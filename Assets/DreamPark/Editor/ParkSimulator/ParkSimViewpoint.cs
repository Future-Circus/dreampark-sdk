// ─────────────────────────────────────────────────────────────────────
//  ParkSimViewpoint.cs — land where you were already looking
//
//  THE JARRING PART OF TURNING THE SIMULATOR ON is that you press Play
//  framed on your attraction and arrive somewhere else entirely: the park
//  is hundreds of metres across, your attraction has been rotated onto a
//  spawn marker on a hillside, and the Scene view is still pointing at
//  wherever the origin used to be. The park is the point, but losing your
//  framing every single time is a tax on the thing creators do most —
//  press Play, look at their attraction, press Stop.
//
//  So the camera's pose is captured RELATIVE TO the attraction you were
//  nearest to before Play, and re-applied relative to wherever that same
//  attraction ends up in the park. Same distance, same angle, same zoom —
//  the shot is identical to the one you had, just with a park around it.
//
//  IT MUST SURVIVE A DOMAIN RELOAD, which is why this goes through
//  SessionState rather than a static: entering Play mode reloads the
//  domain and wipes statics, and the capture happens BEFORE that while the
//  apply happens after. SessionState is also the right lifetime — a
//  viewpoint is meaningless in a later editor session.
//
//  MATCHING IS BY ASSET PATH FIRST. The scene object you were looking at
//  may be renamed ("MyRide (1)"), and if it was a clean prefab instance
//  the park spawns the ASSET, whose name is the prefab's. Asset path is
//  the one identifier that survives both. Name is the fallback, and the
//  first attraction in the park is the fallback to that — landing at some
//  attraction always beats landing at the origin.
// ─────────────────────────────────────────────────────────────────────

using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    internal static class ParkSimViewpoint
    {
        private const string Key = "DreamPark.ParkSim.Viewpoint";
        private const char Sep = '|';

        /// <summary>
        /// Record where the Scene view is looking, expressed in the local space
        /// of the nearest attraction. Called on ExitingEditMode — before the
        /// domain reload, while the creator's untouched scene is still loaded.
        /// </summary>
        public static void Capture()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) { Clear(); return; }

            LevelTemplate nearest = null;
            float nearestSqr = float.MaxValue;

            foreach (var lt in Object.FindObjectsByType<LevelTemplate>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                // Nested templates are part of a bigger attraction, not
                // separately placeable — the park never spawns them alone, so
                // anchoring the camera to one would never resolve.
                if (lt.transform.parent != null &&
                    lt.transform.parent.GetComponentInParent<LevelTemplate>(true) != null) continue;

                float d = (lt.transform.position - view.pivot).sqrMagnitude;
                if (d < nearestSqr) { nearestSqr = d; nearest = lt; }
            }

            if (nearest == null) { Clear(); return; }

            Transform t = nearest.transform;
            Vector3 localPivot = t.InverseTransformPoint(view.pivot);
            Quaternion localRot = Quaternion.Inverse(t.rotation) * view.rotation;

            string assetPath = "";
            if (PrefabUtility.IsPartOfPrefabInstance(nearest.gameObject))
            {
                var root = PrefabUtility.GetOutermostPrefabInstanceRoot(nearest.gameObject);
                if (root != null)
                    assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) ?? "";
            }

            SessionState.SetString(Key, string.Join(Sep.ToString(), new[] {
                assetPath,
                nearest.gameObject.name,
                F(localPivot.x), F(localPivot.y), F(localPivot.z),
                F(localRot.x), F(localRot.y), F(localRot.z), F(localRot.w),
                F(view.size),
            }));
        }

        /// <summary>
        /// Re-frame the Scene view on the same attraction, now that it lives in
        /// the park. Returns the item it locked onto, or null when there was
        /// nothing captured or nothing to match.
        /// </summary>
        public static PlacedItem TryApply(ParkSimReport report)
        {
            if (report == null || report.items.Count == 0) return null;

            var view = SceneView.lastActiveSceneView;
            if (view == null) return null;

            string raw = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(raw)) return null;

            var p = raw.Split(Sep);
            if (p.Length != 10) return null;

            string assetPath = p[0];
            string name = p[1];
            var localPivot = new Vector3(F(p[2]), F(p[3]), F(p[4]));
            var localRot = new Quaternion(F(p[5]), F(p[6]), F(p[7]), F(p[8]));
            float size = F(p[9]);

            // OWNED ITEMS FIRST, at every step. Once the park can come from a
            // source, report.items also holds the park's OWN attractions — and
            // a real park very often contains a published copy of the very
            // thing the creator is editing, under the same name. Falling onto
            // that copy would frame the shipped version of their attraction
            // instead of the one they just changed, which is the single most
            // confusing thing this feature could do.
            PlacedItem match = null;
            if (!string.IsNullOrEmpty(assetPath)) {
                // Only simulator-placed items carry an asset path at all — a
                // source's content came out of a bundle — so this pass is
                // already owned-only, and is listed first because it is the
                // strongest identifier.
                foreach (var item in report.items)
                    if (item.assetPath == assetPath) { match = item; break; }
            }
            if (match == null) match = FirstByName(report, name, true);
            if (match == null) match = FirstByName(report, name, false);
            if (match == null) match = FirstAttraction(report, true);
            if (match == null) {
                // Better to arrive at SOME attraction than at the world origin
                // staring at empty terrain.
                match = FirstAttraction(report, false);
            }
            if (match == null || match.instance == null) return null;

            Transform t = match.instance;
            view.LookAt(t.TransformPoint(localPivot), t.rotation * localRot, size);
            view.Repaint();
            return match;
        }

        private static PlacedItem FirstByName(ParkSimReport report, string name, bool ownedOnly)
        {
            foreach (var item in report.items) {
                if (ownedOnly && !item.simulatorOwned) continue;
                if (item.name == name) return item;
            }
            return null;
        }

        private static PlacedItem FirstAttraction(ParkSimReport report, bool ownedOnly)
        {
            foreach (var item in report.items) {
                if (ownedOnly && !item.simulatorOwned) continue;
                if (item.kind == ContentKind.Attraction) return item;
            }
            return null;
        }

        public static void Clear()
        {
            SessionState.EraseString(Key);
        }

        // Invariant culture on purpose: a machine with a comma decimal
        // separator would round-trip "1,5" and the reparse would silently
        // produce a different camera pose.
        private static string F(float v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static float F(string s)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }
    }
}
