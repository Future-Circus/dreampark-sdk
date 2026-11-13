// ─────────────────────────────────────────────────────────────────────
//  PlaylistJumpPadsAuthoring.cs — the attraction's own JUMP PADS (goal-ui, Oct 20 2026; pl-announce Oct 22: the
//  attraction is the source of truth). Put it on the attraction's AttractionTemplate object, drag the pads in the Scene
//  view (Playlist Studio › Jump Pads, or the inspector). At RUNTIME a goal block's stop.jump{} with no `pads` reads them
//  (PlaylistJumpPads.AuthoredPads): stop.jump{ chain = 'id' } picks this chain when an attraction has several (one
//  component per chain). Explicit stop.jump{pads = …} still wins.
//  Conventions: pads in order start → landing → next landing …, each the spot a player STANDS (the footprints' centre;
//  start prints are drawn 12 cm back from it, landing prints 12 cm past it); positions in this object's local space,
//  y = the floor there (raised platforms: their top). Gizmos: the pads' circles + the arches between them.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace DreamPark
{
    [AddComponentMenu("DreamPark/Playlist/Jump Pads (author)")]
    public sealed class PlaylistJumpPadsAuthoring : MonoBehaviour
    {
        [Tooltip("This chain's id when the attraction has several (stop.jump{ chain = 'id' }); empty = the attraction's default chain.")]
        public string chain = "";
        [Tooltip("Where the player stands, in order: start, landing, next landing … (local; y = the floor there).")]
        public List<Vector3> pads = new List<Vector3> { new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 1f) };
        [Range(0.25f, 1f)] public float radius = PlaylistJumpPads.PadRadius;
        [Range(1f, 5f)] public float window = (float)PlaylistJumpPadRules.DefaultWindow;

        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// The stop's options.pads JSON: [{"x":…,"y":0,"z":…}, …].
        public string OptionsJson()
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < pads.Count; i++) sb.Append(i > 0 ? "," : "").Append($"{{\"x\":{F(pads[i].x)},\"y\":{F(pads[i].y)},\"z\":{F(pads[i].z)}}}");
            return sb.Append("]").ToString();
        }

        /// The goal block call: the attraction's own pads, read at runtime — stop.jump{ chain = '…', fail_on = {'hazard'} }.
        public string GoalLua() => string.IsNullOrEmpty(chain) ? "stop.jump{ fail_on = { 'hazard' } }" : $"stop.jump{{ chain = '{chain}', fail_on = {{ 'hazard' }} }}";

        void OnDrawGizmos()
        {
            var c = MagicUI.MagicLook.VioletBright;
            Gizmos.color = c;
            for (int i = 0; i < pads.Count; i++)
            {
                Vector3 p = transform.TransformPoint(pads[i]);
                const int N = 32;
                for (int k = 0; k < N; k++)
                {
                    float a0 = k * Mathf.PI * 2f / N, a1 = (k + 1) * Mathf.PI * 2f / N;
                    Gizmos.DrawLine(p + new Vector3(Mathf.Cos(a0), 0.02f, Mathf.Sin(a0)) * radius, p + new Vector3(Mathf.Cos(a1), 0.02f, Mathf.Sin(a1)) * radius);
                }
                if (i == 0) continue;
                Vector3 a = transform.TransformPoint(pads[i - 1]);
                float d = Vector3.Distance(a, p), rise = Mathf.Clamp(d * 0.4f, 0.35f, 1.4f);
                Vector3 last = a;
                for (int k = 1; k <= 16; k++)
                {
                    float t = k / 16f;
                    Vector3 q = Vector3.Lerp(a, p, t) + Vector3.up * (4f * rise * t * (1f - t));
                    Gizmos.DrawLine(last, q); last = q;
                }
            }
        }
    }
}
