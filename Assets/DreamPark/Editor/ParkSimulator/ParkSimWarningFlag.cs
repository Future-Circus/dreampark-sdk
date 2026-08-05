// ─────────────────────────────────────────────────────────────────────
//  ParkSimWarningFlag.cs — "this is not what your prefab says"
//
//  When the simulator spawns an attraction from a scene instance carrying
//  un-applied overrides, the developer is testing something that does not
//  exist on disk. That is usually what they want — it is the build on their
//  screen — but it is also exactly the state that produces the "it worked
//  in the editor" bug report, because the version that ships is the prefab.
//  So it is marked, in world space, in the Game view, where it cannot be
//  missed.
//
//  IT LIVES ON THE LEVELANCHOR, NOT THE ATTRACTION. Deliberate:
//  LevelObjectManager.RegisterLevelObject is called on the attraction and
//  builds a LevelObject for every child under it, so a flag parented to the
//  attraction would be registered as park content, get parked by the
//  optimizer along with everything else, and vanish at exactly the distance
//  the developer is most likely to be standing. As a sibling under the
//  LevelAnchor it is never registered at all, with OptimizedAFIgnore as
//  belt and braces on top.
//
//  IT IS NOT ON THE GIZMO LAYER, which is where a warning marker would
//  otherwise belong. Simulator.SpawnCamera strips both Gizmo and
//  SecondaryRenderer out of the editor camera's cullingMask, so a flag on
//  that layer renders in the Scene view and is INVISIBLE in the Game view —
//  which is the one view a developer is actually looking at while the park
//  runs, and the one place this warning has to appear.
//
//  BUILT FROM PRIMITIVES, not a prefab or a font asset, so it has no
//  dependency that a creator project could be missing: a three-vertex mesh
//  for the triangle and TextMesh with a built-in font.
// ─────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace DreamPark.ParkSim
{
    public class ParkSimWarningFlag : MonoBehaviour
    {
        private const float HoverMeters = 1.2f;
        private static readonly Color CautionYellow = new Color(1f, 0.78f, 0.05f, 1f);

        private Transform _billboard;

        public static void Attach(Transform levelAnchor, GameObject subject, string displayName)
        {
            if (levelAnchor == null || subject == null) return;

            var host = new GameObject("[ParkSim] Warning — " + displayName);
            host.transform.SetParent(levelAnchor, false);

            // Default layer, deliberately — see the header. Nothing culls it.
            host.layer = 0;
            host.AddComponent<OptimizedAFIgnore>();

            // Sit above the attraction's actual silhouette rather than a fixed
            // height, so it clears a Jumbo without floating absurdly over a
            // small prop.
            float top = TopOf(subject);
            host.transform.position = new Vector3(
                subject.transform.position.x,
                top + HoverMeters,
                subject.transform.position.z);

            var flag = host.AddComponent<ParkSimWarningFlag>();
            flag.Build(displayName);
        }

        private void Build(string displayName)
        {
            _billboard = new GameObject("Billboard").transform;
            _billboard.SetParent(transform, false);
            _billboard.gameObject.layer = 0;

            BuildTriangle();
            BuildLabel("!", 0.34f, Color.black, new Vector3(0f, 0.18f, -0.01f));
            BuildLabel(
                "UNAPPLIED SCENE CHANGES\n" + displayName,
                0.11f, CautionYellow, new Vector3(0f, -0.28f, -0.01f));
        }

        private void BuildTriangle()
        {
            var go = new GameObject("Caution");
            go.transform.SetParent(_billboard, false);
            go.layer = 0;

            var mesh = new Mesh {
                name = "ParkSimCaution",
                vertices = new[] {
                    new Vector3(-0.55f, -0.42f, 0f),
                    new Vector3( 0.55f, -0.42f, 0f),
                    new Vector3( 0f,     0.62f, 0f),
                },
                triangles = new[] { 0, 1, 2 },
                colors = new[] { CautionYellow, CautionYellow, CautionYellow },
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            // Sprites/Default is unlit, honours vertex colour, and is always
            // present — the same shader LevelTemplate uses for its own runtime
            // line renderers, for the same reason.
            var mr = go.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader != null) {
                mr.material = new Material(shader) { color = CautionYellow };
            }
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private void BuildLabel(string text, float size, Color color, Vector3 offset)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(_billboard, false);
            go.transform.localPosition = offset;
            go.layer = 0;

            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = size;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;

            var font = BuiltinFont();
            if (font != null) {
                tm.font = font;
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterial = font.material;
            }
        }

        /// Unity renamed the built-in font between versions and TextMesh renders
        /// nothing at all without one, so try both names before giving up.
        private static Font BuiltinFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        private static float TopOf(GameObject subject)
        {
            var renderers = subject.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return subject.transform.position.y;

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds.max.y;
        }

        private void LateUpdate()
        {
            if (_billboard == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            // Yaw-only billboard. A full LookRotation would tip the sign
            // backwards whenever the guest looks up at it, which reads as a
            // glitch rather than a warning.
            Vector3 toCam = cam.transform.position - _billboard.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-4f) return;
            _billboard.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
        }
    }
}
