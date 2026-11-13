using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>
    /// PANELS ON TOP (Aidan, Oct 21 2026): "all UI panels must be visible above occluders and 3D objects, so players
    /// never miss important details". Covered: the boards (start, standings, Results + its recap / badges / rating /
    /// What's Next, the personal card, the podium sign), the lock sign when it shows, and Dreamie's caption. From
    /// Oct 22 also the Quest load cards, the pause card, the Quest diorama sync card and the boot constellation cards.
    /// The ONLY exception is the objective markers (PlaylistGoalMarkers), which hide behind walls; they never carry
    /// this component. The podium is a 3D celebration, not a panel.
    /// Oct 22 (Aidan: "...but if we can still have our hands occlude over the panels thats ideal"): the player's real
    /// hand still covers a panel. That's Meta environment depth gated by the tracked-hand capsules
    /// (DreamParkHandOcclusion.hlsl, capsules from DepthMaskCeiling); never a hand / forearm mesh occluder.
    ///
    /// A panel opts in on its root. It changes draw order and depth only, never the look:
    ///   • depth: ZTest Always in the overlay queues (glass 4086…4120, words 4125, so glass stays behind its text and
    ///     icons), re-applied to parts added later (rows, stickers, the recap card, a lock sign's requirement row);
    ///   • after the world: every renderer of the panel draws with sorting order <see cref="SortingBase"/>+, which this
    ///     project's URP transparent pass sorts BEFORE render queue. So the panel draws after every world transparent,
    ///     wall / depth-only occluder and title effect at any queue (probe: Tools/Builders/UiOnTopProbe.cs);
    ///   • between panels: the nearest panel to the viewer gets the highest order (re-ranked each frame);
    ///   • hands only: its words move to the DreamPark TMP SDF shader with _DPHandOnly (no wall / furniture depth), and
    ///     its HoloParticle / sprite parts get _DPHandOcclude. In the Editor and on iOS that is a no-op.
    /// Two ways in:
    ///   • <see cref="Of"/>: a Magic UI panel (MagicBoard, the lock sign, Dreamie's caption). Its materials are its own
    ///     runtime materials and its code fades them, so they change in place (<see cref="MagicLook.SetXray"/>);
    ///   • <see cref="Panel"/>: any other panel (load cards, pause card, sync card, constellation). Its materials can
    ///     be shared with non-panel renderers or be project assets, so each one is swapped for an on-top COPY (cached
    ///     per source; sprites with no depth-test property get DreamPark/SpriteOnTop), re-swapped if its code puts the
    ///     original back. Property blocks (per-card art, colours) still apply.
    /// ONLY IN THE VIEWER'S ROOM (Aidan, Oct 22: "Not sure why any cards/panels would be visible between rooms"): a panel
    /// draws over everything only while it stands in the wearer's current room (<see cref="InViewerRoom"/>, the core's
    /// space map / room graph). A panel or card anchored in another room keeps normal depth, so walls hide it, and its
    /// words get the normal Meta depth occlusion back. Checked every 0.25 s, so walking through a door switches it.
    /// Particles (motes, edge glints, bursts) are untouched: they keep their own queue and depth test, as in every
    /// x-ray panel before (the approved card look). Stereo: nothing per eye; the same materials and order for both.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MagicOnTop : MonoBehaviour
    {
        /// Kill switch (harness before/after stills). Read when a panel is built.
        public static bool Enabled = true;
        /// Above every world renderer's order in layer (0), below screen overlays (the pause canvas, 32000).
        public const int SortingBase = 20000;

        static readonly int HandsOnId = Shader.PropertyToID("_DPHandsOn");
        static bool s_hands = true;
        /// Real hands cover panels (global; harness before/after). On by default.
        public static bool Hands
        {
            get => s_hands;
            set { s_hands = value; Shader.SetGlobalFloat(HandsOnId, value ? 1f : 0f); }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InitGlobals() => Hands = s_hands;

        /// (eye, panel) world positions: true when the panel stands in the viewer's current room. Set by the core
        /// (MagicOnTopRooms: space map rooms / rects, a real wall between them when either is outside every room).
        /// Null (an SDK title's own editor, no space map) = always.
        public static Func<Vector3, Vector3, bool> InViewerRoom;
        /// The room rule on (harness before/after). On by default.
        public static bool RoomRule = true;

        static readonly List<MagicOnTop> s_live = new List<MagicOnTop>();
        static readonly List<(float d, MagicOnTop p)> s_rank = new List<(float, MagicOnTop)>();
        static int s_rankedFrame = -1;

        readonly List<Renderer> _rends = new List<Renderer>();
        int _count = -1, _order = int.MinValue, _want = SortingBase;
        float _again;
        bool _copies, _active = true;
        MagicBoard _board;
        readonly Dictionary<Renderer, (Material[] mats, int order)> _orig = new Dictionary<Renderer, (Material[], int)>();

        /// On top right now (in the viewer's room); false = normal depth (another room).
        public bool Active => _active;

        /// A personal card that follows the viewer: it is always in the viewer's room, so the room rule never applies
        /// (a park standing in the wrong place, or a wall right in front of the wearer, used to put normal depth back on it).
        public bool FollowsViewer;

        /// Put a Magic UI panel on top (idempotent; its own materials change in place). Null when switched off.
        public static MagicOnTop Of(Transform root) => Make(root, false);

        /// Put any other panel on top (idempotent; its materials are swapped for cached on-top copies). Null when off.
        public static MagicOnTop Panel(Transform root) => Make(root, true);

        static MagicOnTop Make(Transform root, bool copies)
        {
            if (root == null || !Enabled) return null;
            var p = root.GetComponent<MagicOnTop>();
            if (p == null) p = root.gameObject.AddComponent<MagicOnTop>();
            p._copies = copies;
            p._board = root.GetComponent<MagicBoard>();
            p._active = p.WantActive();
            p.Refresh();
            return p;
        }

        /// True when <paramref name="t"/> is part of a panel drawn on top.
        public static bool Under(Transform t) => t != null && t.GetComponentInParent<MagicOnTop>(true) != null;

        /// The sorting order this panel draws with now (checks).
        public int Order => _order;

        void OnEnable() { if (!s_live.Contains(this)) s_live.Add(this); _count = -1; }
        void OnDisable() => s_live.Remove(this);

        void LateUpdate()
        {
            if (Time.frameCount != s_rankedFrame) Rank();
            // The viewer's room; parts added after the panel was built (rows, stickers, the recap card, What's Next, a lock
            // sign's row); a foreign panel's code putting its original materials back (load cards rebinding to the shell's).
            if (Time.unscaledTime >= _again)
            {
                _again = Time.unscaledTime + 0.25f;
                bool want = WantActive();
                if (want != _active) { _active = want; Refresh(); }
                else if (transform.hierarchyCount != _count || (_copies && _active && Reverted())) Refresh();
            }
            if (_active && _want != _order) ApplyOrder(_want);
        }

        bool WantActive()
        {
            if (FollowsViewer || !RoomRule || InViewerRoom == null) return true;
            Camera cam = Camera.main;
            if (cam == null) return true;
            try { return InViewerRoom(cam.transform.position, transform.position); } catch (Exception) { return true; }
        }

        void Refresh()
        {
            _count = transform.hierarchyCount;
            if (!_copies)
            {
                if (_board != null) _board.SetXray(_active);   // the board's own x-ray (it re-applies to parts added later)
                else MagicLook.SetXray(transform, _active);
            }
            _rends.Clear();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                if (r.GetComponentInParent<MagicOnTop>(true) != this) continue;   // a nested panel orders itself
                _rends.Add(r);
                if (!_orig.ContainsKey(r)) _orig[r] = (r.sharedMaterials, r.sortingOrder);
                var tmp = r.GetComponent<TMP_Text>();
                if (tmp != null)
                {
                    if (_active) Words(tmp, _copies);
                    else PlainWords(tmp, _copies);
                    continue;
                }
                if (_copies)
                {
                    if (_active) Swap(r);
                    else Restore(r);
                }
                else foreach (Material m in r.sharedMaterials) if (m != null && m.HasProperty(HandOccludeId)) m.SetFloat(HandOccludeId, _active ? 1f : 0f);
            }
            _order = int.MinValue;
            if (_active) ApplyOrder(_want);
            else foreach (Renderer r in _rends) if (r != null && _orig.TryGetValue(r, out var o) && r.sortingOrder != o.order) r.sortingOrder = o.order;
        }

        void ApplyOrder(int order)
        {
            _order = order;
            foreach (Renderer r in _rends) if (r != null && r.sortingOrder != order) r.sortingOrder = order;
        }

        /// Another room: the label depth-tested again at the normal text queue, and the full Meta depth occlusion back.
        static void PlainWords(TMP_Text t, bool setDepth)
        {
            Material m = t.fontMaterial;
            if (m == null) return;
            if (setDepth)
            {
                m.SetFloat(GuiZTest, (float)UnityEngine.Rendering.CompareFunction.LessEqual);
                m.renderQueue = 3000;
            }
            if (m.HasProperty(HandOnlyId)) m.SetFloat(HandOnlyId, 0f);
        }

        void Restore(Renderer r)
        {
            if (!_orig.TryGetValue(r, out var o) || o.mats == null) return;
            var cur = r.sharedMaterials;
            bool same = cur.Length == o.mats.Length;
            for (int i = 0; same && i < cur.Length; i++) same = cur[i] == o.mats[i];
            if (!same) r.sharedMaterials = o.mats;
        }

        /// Farthest panel first (lowest order), nearest last, from the viewer's camera.
        static void Rank()
        {
            s_rankedFrame = Time.frameCount;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            s_rank.Clear();
            foreach (MagicOnTop p in s_live) if (p != null && p._active) s_rank.Add(((p.transform.position - eye).sqrMagnitude, p));
            s_rank.Sort((a, b) => b.d.CompareTo(a.d));
            for (int i = 0; i < s_rank.Count; i++) s_rank[i].p._want = SortingBase + i;
        }

        // ── Words: on top, and only real hands cover them ───────────

        static readonly int HandOnlyId = Shader.PropertyToID("_DPHandOnly");
        static readonly int HandOccludeId = Shader.PropertyToID("_DPHandOcclude");
        const string GuiZTest = "unity_GUIZTestMode";

        /// The label's own material instance: ZTest Always at the words queue (foreign panels; Magic UI panels already
        /// got it from SetXray), moved onto the DreamPark TMP SDF shader with hands-only occlusion.
        static void Words(TMP_Text t, bool setDepth)
        {
            Material m = t.fontMaterial;   // a per-label instance
            if (m == null) return;
            if (setDepth)
            {
                m.SetFloat(GuiZTest, (float)UnityEngine.Rendering.CompareFunction.Always);
                m.renderQueue = MagicLook.XrayTextQueue;
            }
            string name = DreamParkTmpOcclusion.OccludedShaderNameFor(m.shader != null ? m.shader.name : null);
            Shader sh = name != null ? Shader.Find(name) : null;
            if (sh != null && sh.isSupported)
            {
                string[] kw = m.shaderKeywords;
                int queue = m.renderQueue;
                float zt = m.GetFloat(GuiZTest);
                m.shader = sh;
                m.shaderKeywords = kw;
                m.renderQueue = queue;
                m.SetFloat(GuiZTest, zt);
            }
            if (m.HasProperty(HandOnlyId)) m.SetFloat(HandOnlyId, 1f);
        }

        // ── Foreign panels: on-top copies of their materials ────────

        static readonly Dictionary<Material, Material> s_copyOf = new Dictionary<Material, Material>();
        static readonly HashSet<Material> s_isCopy = new HashSet<Material>();
        static Shader s_sprite;
        static bool s_spriteLooked;

        static Material OnTopCopy(Material src, bool sprite)
        {
            if (src == null || s_isCopy.Contains(src)) return src;
            if (s_copyOf.TryGetValue(src, out Material c) && c != null) return c;
            if (src.HasProperty("_ZTest"))
            {
                c = new Material(src);
                c.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            }
            else if (sprite)
            {
                if (!s_spriteLooked) { s_spriteLooked = true; s_sprite = Shader.Find("DreamPark/SpriteOnTop"); }
                if (s_sprite == null) return src;
                c = new Material(s_sprite);
                if (src.HasProperty("_Color")) c.SetColor("_Color", src.GetColor("_Color"));
            }
            else
            {
                Debug.LogWarning($"[MagicOnTop] '{src.name}' ({src.shader?.name}) has no depth-test property: left as it was.");
                s_copyOf[src] = src;
                return src;
            }
            c.renderQueue = Mathf.Min(4999, src.renderQueue + MagicLook.XrayLift);
            if (c.HasProperty(HandOccludeId)) c.SetFloat(HandOccludeId, 1f);
            c.name = src.name + " (on top)";
            c.hideFlags = HideFlags.DontSave;
            s_copyOf[src] = c;
            s_isCopy.Add(c);
            return c;
        }

        static readonly List<Material> s_mats = new List<Material>();

        static void Swap(Renderer r)
        {
            r.GetSharedMaterials(s_mats);
            bool changed = false;
            for (int i = 0; i < s_mats.Count; i++)
            {
                Material c = OnTopCopy(s_mats[i], r is SpriteRenderer);
                if (c != s_mats[i]) { s_mats[i] = c; changed = true; }
            }
            if (changed) r.sharedMaterials = s_mats.ToArray();
        }

        bool Reverted()
        {
            foreach (Renderer r in _rends)
            {
                if (r == null) continue;
                var t = r.GetComponent<TMP_Text>();
                if (t != null)
                {
                    // A label whose code assigned a shared font material again (a style re-applied).
                    Material f = t.fontSharedMaterial;
                    if (f != null && f.HasProperty(HandOnlyId) == false && DreamParkTmpOcclusion.OccludedShaderNameFor(f.shader != null ? f.shader.name : null) != null) return true;
                    continue;
                }
                Material m = r.sharedMaterial;
                if (m != null && !s_isCopy.Contains(m) && !(s_copyOf.TryGetValue(m, out Material c) && c == m)) return true;
            }
            return false;
        }
    }
}
