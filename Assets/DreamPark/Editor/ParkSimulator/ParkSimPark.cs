// ─────────────────────────────────────────────────────────────────────
//  ParkSimPark.cs — the fake venue
//
//  Stands in for everything the headset would normally supply: the ground
//  the guest walks on, the mesh calibration raycasts against, and the
//  operator's placement decisions.
//
//  THE MESH GOES ON THE ARMesh LAYER. That is not decoration — it is the
//  entire mechanism. CalibrateLevel and CalibrateProp raycast against
//  LayerMask.GetMask("ARMesh"), so putting park.fbx on that layer with
//  real MeshColliders is what makes an attraction's floor grid conform to
//  terrain exactly the way it conforms to a scanned room. Nothing else in
//  the simulator has to know calibration exists.
//
//  THE MARKERS ARE FLAT AND THE GROUND IS NOT. All 17 Empty nodes in
//  park.fbx sit at park-local Y = 0 while the terrain under them ranges
//  roughly -20m to +27m. Using a marker's authored position directly would
//  bury half the attractions and float the other half, so every marker is
//  dropped onto the mesh before it is used. This is the simulator's stand-in
//  for an operator placing an attraction on a real floor, and it has to
//  happen BEFORE calibration or the floor grid would be measuring relief
//  from the wrong elevation.
//
//  ROTATION IS YAW-ONLY. Attractions stand upright on sloped ground in the
//  real world; it is the floor MESH that follows the grade, via
//  CalibrateLevel. Tilting the attraction itself would double-apply the
//  slope and is not something that can happen in a real park.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    public struct SpawnPoint
    {
        public string markerName;
        public Vector3 position;
        public Quaternion rotation;
        /// False when the drop found no ground under the marker — the marker is
        /// still usable, it just sits at its authored elevation.
        public bool grounded;
    }

    public static class ParkSimPark
    {
        /// Where the park mesh has lived at various points. Tried in order
        /// before falling back to a project-wide search, so the common case
        /// costs one AssetDatabase lookup rather than a scan.
        private static readonly string[] KnownParkPaths = {
            "Assets/DreamPark/Resources/Park/park.fbx",
            "Assets/DreamPark/Models/Park.fbx",
            "Assets/Resources/Park/park.fbx",
            "Assets/Resources/park.fbx",
        };

        private static string _resolvedParkPath;
        public const string EnvironmentName = "[ParkSim] Park Environment";
        private const string ARMeshLayerName = "ARMesh";
        private const float DropProbeRange = 400f;

        /// <summary>
        /// Instantiate park.fbx as the environment mesh: ARMesh layer top to
        /// bottom, a MeshCollider on every renderer, and excluded from
        /// OptimizedAF so the venue itself never gets culled out from under the
        /// guest.
        /// </summary>
        public static GameObject SpawnEnvironment(List<string> notes)
        {
            EnsureMeshIsReadable(notes);

            string parkPath = ResolveParkAssetPath();
            var asset = string.IsNullOrEmpty(parkPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(parkPath);

            if (asset == null) {
                notes.Add("Could not find a park mesh anywhere in the project (looked for an FBX " +
                          "named \"park\") — no environment mesh, so nothing will calibrate.");
                return null;
            }

            var env = Object.Instantiate(asset);
            env.name = EnvironmentName;
            env.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            int arMeshLayer = LayerMask.NameToLayer(ARMeshLayerName);
            if (arMeshLayer < 0) {
                notes.Add("Layer \"" + ARMeshLayerName + "\" is missing from this project. " +
                          "Calibration raycasts against it, so no attraction will conform. " +
                          "Run DreamPark > Sync Tags & Layers from Core.");
            }

            int colliders = 0;
            foreach (var t in env.GetComponentsInChildren<Transform>(true)) {
                if (arMeshLayer >= 0) t.gameObject.layer = arMeshLayer;

                var mf = t.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                if (t.GetComponent<Collider>() != null) continue;

                var mc = t.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                colliders++;
            }

            if (colliders == 0) {
                notes.Add("park.fbx produced no mesh colliders — calibration has nothing to hit.");
            }

            // The venue is scenery, not gameplay. Without this the optimizer
            // would happily park the ground the guest is standing on.
            if (env.GetComponent<OptimizedAFIgnore>() == null) {
                env.AddComponent<OptimizedAFIgnore>();
            }

            return env;
        }

        /// <summary>
        /// park.fbx ships with Read/Write disabled, which is correct for a
        /// runtime asset but stops a runtime-assigned MeshCollider from getting
        /// physics data in a player build. The simulator is editor-only so it
        /// works either way, but flipping it keeps behaviour identical if this
        /// mesh is ever used outside the editor, and it is our own SDK asset to
        /// flip. One reimport, once.
        /// </summary>
        private static void EnsureMeshIsReadable(List<string> notes)
        {
            string path = ResolveParkAssetPath();
            if (string.IsNullOrEmpty(path)) return;

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null || importer.isReadable) return;

            importer.isReadable = true;
            importer.SaveAndReimport();
            notes.Add("Enabled Read/Write on " + path + " so its mesh colliders carry physics data.");
        }

        /// <summary>
        /// Find the park mesh wherever it currently lives.
        ///
        /// Deliberately AssetDatabase, NOT Resources.Load. The simulator is
        /// editor-only, so it has no reason to require the mesh sit in a
        /// Resources folder — and a positive reason not to, since everything
        /// under Resources is force-included in every player build a creator
        /// makes, and this mesh is several megabytes of scenery they will
        /// never ship. AssetDatabase also means moving the asset cannot break
        /// the simulator again: the known paths are a fast path, and a
        /// project-wide search is the backstop.
        /// </summary>
        public static string ResolveParkAssetPath()
        {
            if (!string.IsNullOrEmpty(_resolvedParkPath) &&
                AssetDatabase.LoadAssetAtPath<GameObject>(_resolvedParkPath) != null) {
                return _resolvedParkPath;
            }

            foreach (var path in KnownParkPaths) {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) {
                    _resolvedParkPath = path;
                    return path;
                }
            }

            // Fall back to a search. "t:Model" keeps this to imported meshes,
            // and the filename is re-checked exactly because Unity's name
            // filter matches substrings — "dreampark-alt-dp" would otherwise
            // qualify.
            foreach (var guid in AssetDatabase.FindAssets("park t:Model")) {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!System.IO.Path.GetFileNameWithoutExtension(path)
                        .Equals("park", System.StringComparison.OrdinalIgnoreCase)) continue;
                _resolvedParkPath = path;
                return path;
            }

            _resolvedParkPath = null;
            return null;
        }

        /// <summary>
        /// Read the Empty markers out of the instantiated environment and drop
        /// each onto the mesh below it. Order is randomised by
        /// <paramref name="seed"/> — that shuffle is what "Regenerate" mixes up.
        /// </summary>
        public static List<SpawnPoint> CollectSpawnPoints(GameObject environment, int seed, List<string> notes)
        {
            var points = new List<SpawnPoint>();
            if (environment == null) return points;

            int arMeshLayer = LayerMask.NameToLayer(ARMeshLayerName);
            LayerMask groundMask = arMeshLayer >= 0 ? (LayerMask)(1 << arMeshLayer) : (LayerMask)0;
            LayerMask noPrior = (LayerMask)0;
            var span = GroundProbe.Span.Of(DropProbeRange, DropProbeRange);

            int ungrounded = 0;

            foreach (var t in environment.GetComponentsInChildren<Transform>(true)) {
                if (!t.name.StartsWith("Empty", System.StringComparison.OrdinalIgnoreCase)) continue;
                // Markers are Nulls in the FBX; anything carrying geometry with
                // an "Empty" name is scenery that happens to be badly named.
                if (t.GetComponent<MeshFilter>() != null) continue;

                Vector3 pos = t.position;
                bool grounded = false;

                if (groundMask.value != 0 &&
                    GroundProbe.TryFindGround(pos, span, groundMask, noPrior, out RaycastHit hit)) {
                    pos = hit.point;
                    grounded = true;
                } else {
                    ungrounded++;
                }

                points.Add(new SpawnPoint {
                    markerName = t.name,
                    position = pos,
                    rotation = YawOnly(t),
                    grounded = grounded,
                });
            }

            if (points.Count == 0) {
                notes.Add("No Empty* markers found on the park mesh — attractions will stack at the origin.");
            }
            if (ungrounded > 0) {
                notes.Add(ungrounded + " of " + points.Count + " spawn markers found no ground beneath them " +
                          "and kept their authored elevation.");
            }

            Shuffle(points, seed);
            return points;
        }

        /// <summary>
        /// Drop a world position onto the park mesh. Same probe the spawn
        /// markers use, exposed so derived placements (props clustered beside
        /// an attraction) land on the ground rather than at their host's
        /// elevation on sloped terrain.
        /// </summary>
        public static Vector3 DropToGround(Vector3 worldPos)
        {
            int arMeshLayer = LayerMask.NameToLayer(ARMeshLayerName);
            if (arMeshLayer < 0) return worldPos;

            LayerMask groundMask = (LayerMask)(1 << arMeshLayer);
            var span = GroundProbe.Span.Of(DropProbeRange, DropProbeRange);
            if (GroundProbe.TryFindGround(worldPos, span, groundMask, (LayerMask)0, out RaycastHit hit)) {
                return hit.point;
            }
            return worldPos;
        }

        /// Flatten a marker's orientation to a yaw. The markers come out of
        /// Blender where the up axis is Z, so depending on how each one was
        /// authored its Unity forward may be pointing at the sky; fall back
        /// through up, then to world forward, rather than emitting a degenerate
        /// LookRotation.
        private static Quaternion YawOnly(Transform t)
        {
            Vector3 fwd = t.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { fwd = t.up; fwd.y = 0f; }
            if (fwd.sqrMagnitude < 1e-4f) { fwd = Vector3.forward; }
            return Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }

        private static void Shuffle(List<SpawnPoint> list, int seed)
        {
            var rng = new System.Random(seed);
            for (int i = list.Count - 1; i > 0; i--) {
                int j = rng.Next(i + 1);
                var tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }
    }
}
