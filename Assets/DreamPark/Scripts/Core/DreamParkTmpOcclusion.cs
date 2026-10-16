using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;

namespace DreamPark
{
    /// <summary>
    /// World-space TextMesh Pro text in content is occluded by the real room (Oct 2026).
    ///
    /// THE BUG. TMP's stock shaders ("TextMeshPro/Distance Field", "TextMeshPro/Mobile/Distance
    /// Field") have no Meta Depth API integration, so on a Quest in passthrough every sign,
    /// score and label in every title drew over the guest's hands, furniture and walls. The fix
    /// is two shaders, "DreamPark/TextMeshPro/Distance Field" and "DreamPark/TextMeshPro/Mobile/
    /// Distance Field" (Assets/DreamPark/Shaders/DreamPark-TMP_SDF*.shader): TMP's own SDF maths
    /// and properties plus the same HARD_OCCLUSION / SOFT_OCCLUSION macros DreamPark/Particles
    /// runs. The SDK's default font materials ship on them, and the pre-upload check
    /// "meta-occlusion" offers a one-click repoint for a creator's own TMP materials.
    ///
    /// THIS CLASS is the part that needs no re-upload: content that already shipped (or was
    /// never re-pointed) on a stock TMP SDF shader is moved onto the DreamPark one at runtime.
    ///
    ///   • WHAT: a <see cref="TMP_Text"/> whose shared font material uses one of the two stock
    ///     SDF shaders, AND which is world-space (a 3D <see cref="TextMeshPro"/>, or a
    ///     <see cref="TextMeshProUGUI"/> on a World Space canvas), AND which sits inside content
    ///     (has a <see cref="GameArea"/> ancestor — every attraction and prop root has one), AND
    ///     is not part of a <see cref="PlayerRig"/> (HUDs, wrist and hand-held labels stay
    ///     unoccluded: they ride the head/hands and would flicker against the guest's own arm).
    ///     Screen-space UI, the app's own UI, overlay/SSD/bitmap/surface TMP shaders and sprite
    ///     sub-meshes are never touched.
    ///   • HOW: the text gets a CLONE of its material on the DreamPark shader (same textures,
    ///     properties, keywords and render queue), cached per source material — the shared source
    ///     is never modified, so a screen-space UGUI text using the same font keeps its stock
    ///     material. TMP's fallback-font materials are derived from the text's material, so they
    ///     follow automatically.
    ///   • WHEN: TMP raises TMPro_EventManager.TEXT_CHANGED_EVENT every time it (re)generates a
    ///     text mesh — in builds too. The handler only queues (a dictionary lookup on the shader);
    ///     the swap runs from a PreLateUpdate player-loop step, never inside TMP's or UGUI's
    ///     rebuild loop (UGUI rejects a re-dirty from inside its graphic rebuild). A text is
    ///     handled once: after the swap its shader is no longer a stock one.
    ///   • COST: per text regeneration, one reference read and one dictionary lookup. The scope
    ///     test (two GetComponentInParent walks) runs once per text, and a rejection is cached
    ///     against the text's root transform so it is re-tested only if the text is re-parented
    ///     (park content is parented after Instantiate).
    ///
    /// The DreamPark shaders are in the app build because the SDK's default TMP materials under
    /// Assets/DreamPark/ThirdParty/TextMesh Pro/Resources/ reference them, and their feature
    /// keywords are multi_compile, so every outline/underlay/bevel/glow variant exists. If a
    /// shader is missing (an older app, a stripped build) <see cref="Shader.Find"/> returns null
    /// and nothing is swapped. In the Editor and on iOS the occlusion keywords are off and the
    /// swapped text renders identically. Kill switch: <see cref="Enabled"/>.
    /// </summary>
    public static class DreamParkTmpOcclusion
    {
        public const string StockSdf = "TextMeshPro/Distance Field";
        public const string StockSdfMobile = "TextMeshPro/Mobile/Distance Field";
        public const string OccludedSdf = "DreamPark/TextMeshPro/Distance Field";
        public const string OccludedSdfMobile = "DreamPark/TextMeshPro/Mobile/Distance Field";

        /// <summary>Kill switch. False = no new swaps (texts already swapped keep their material).</summary>
        public static bool Enabled = true;

        /// <summary>Texts moved onto a DreamPark TMP shader since start-up (diagnostics).</summary>
        public static int SwappedCount { get; private set; }

        /// <summary>
        /// The occluded DreamPark shader name for a stock TMP SDF shader name, or null when the
        /// shader is not one this mapping covers (Overlay, SSD, Bitmap, Surface, Masking,
        /// 2-Pass, Sprite and Shader Graph TMP shaders are deliberately left alone).
        /// </summary>
        public static string OccludedShaderNameFor(string shaderName)
        {
            switch (shaderName)
            {
                case StockSdf: return OccludedSdf;
                case StockSdfMobile: return OccludedSdfMobile;
                default: return null;
            }
        }

        public static bool IsOccludedTmpShader(string shaderName)
        {
            return shaderName == OccludedSdf || shaderName == OccludedSdfMobile;
        }

        // stock shader → DreamPark shader (null = not mapped, or the DreamPark shader is missing)
        static readonly Dictionary<Shader, Shader> s_targets = new Dictionary<Shader, Shader>();
        // source material → its clone on the DreamPark shader
        static readonly Dictionary<Material, Material> s_clones = new Dictionary<Material, Material>();
        // text instance id → root transform instance id it was rejected under
        static readonly Dictionary<int, int> s_rejectedUnderRoot = new Dictionary<int, int>();
        static readonly List<TMP_Text> s_pending = new List<TMP_Text>();
        static readonly HashSet<int> s_pendingIds = new HashSet<int>();
        static int s_clonesSincePurge;

        struct FlushStep { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // With domain reload disabled in the Editor, statics survive into the next Play.
            DestroyClones();
            s_targets.Clear();
            s_rejectedUnderRoot.Clear();
            s_pending.Clear();
            s_pendingIds.Clear();
            SwappedCount = 0;
            s_clonesSincePurge = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);

            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Application.quitting -= DestroyClones;
            Application.quitting += DestroyClones;

            InsertFlushStep();
        }

        static void InsertFlushStep()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (root.subSystemList == null) return;
            for (int i = 0; i < root.subSystemList.Length; i++)
            {
                if (root.subSystemList[i].type != typeof(PreLateUpdate)) continue;
                PlayerLoopSystem phase = root.subSystemList[i];
                PlayerLoopSystem[] subs = phase.subSystemList ?? new PlayerLoopSystem[0];
                for (int j = 0; j < subs.Length; j++)
                    if (subs[j].type == typeof(FlushStep)) return;   // already installed
                var grown = new PlayerLoopSystem[subs.Length + 1];
                subs.CopyTo(grown, 0);
                grown[subs.Length] = new PlayerLoopSystem { type = typeof(FlushStep), updateDelegate = Flush };
                phase.subSystemList = grown;
                root.subSystemList[i] = phase;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
        }

        static void OnTextChanged(Object obj)
        {
            // Runs inside TMP's mesh generation: queue only, and never throw into TMP.
            if (!Enabled) return;
            try
            {
                var text = obj as TMP_Text;
                if (text == null) return;
                Material mat = text.fontSharedMaterial;
                if (mat == null || TargetFor(mat.shader) == null) return;
                if (s_pendingIds.Add(text.GetInstanceID())) s_pending.Add(text);
            }
            catch (System.Exception) { }
        }

        static void Flush()
        {
            if (s_pending.Count == 0) return;
            for (int i = 0; i < s_pending.Count; i++)
            {
                TMP_Text text = s_pending[i];
                if (text == null) continue;
                try { TrySwap(text); }
                catch (System.Exception e) { Debug.LogWarning("[DreamParkTmpOcclusion] " + e.Message, text); }
            }
            s_pending.Clear();
            s_pendingIds.Clear();
        }

        /// <summary>
        /// Moves one text onto the occluded shader now if it qualifies (see the class summary).
        /// Returns true when it swapped. Safe to call outside TMP's rebuild; the runtime path
        /// calls it from the player loop.
        /// </summary>
        public static bool TrySwap(TMP_Text text)
        {
            if (!Enabled || text == null) return false;
            Material mat = text.fontSharedMaterial;
            if (mat == null) return false;
            Shader target = TargetFor(mat.shader);
            if (target == null) return false;

            int id = text.GetInstanceID();
            int rootId = text.transform.root.GetInstanceID();
            int rejectedRoot;
            if (s_rejectedUnderRoot.TryGetValue(id, out rejectedRoot) && rejectedRoot == rootId) return false;
            if (!IsInScope(text))
            {
                s_rejectedUnderRoot[id] = rootId;
                return false;
            }
            s_rejectedUnderRoot.Remove(id);

            text.fontSharedMaterial = GetOccludedMaterial(mat, target);
            SwappedCount++;
            return true;
        }

        /// <summary>
        /// World-space content text: 3D TextMeshPro, or UGUI on a World Space canvas; under a
        /// GameArea (attraction/prop); not under a PlayerRig.
        /// </summary>
        public static bool IsInScope(TMP_Text text)
        {
            if (text == null) return false;
            if (!(text is TextMeshPro))
            {
                var ugui = text as TextMeshProUGUI;
                if (ugui == null) return false;
                Canvas canvas = ugui.canvas;
                if (canvas == null || canvas.rootCanvas.renderMode != RenderMode.WorldSpace) return false;
            }
            if (text.GetComponentInParent<GameArea>(true) == null) return false;
            if (text.GetComponentInParent<PlayerRig>(true) != null) return false;
            return true;
        }

        /// <summary>
        /// The cached clone of <paramref name="source"/> on its DreamPark TMP shader (created on
        /// first use), or <paramref name="source"/> itself when it is not on a mapped stock shader.
        /// </summary>
        public static Material GetOccludedMaterial(Material source)
        {
            if (source == null) return null;
            Shader target = TargetFor(source.shader);
            return target == null ? source : GetOccludedMaterial(source, target);
        }

        static Material GetOccludedMaterial(Material source, Shader target)
        {
            Material clone;
            if (s_clones.TryGetValue(source, out clone) && clone != null) return clone;

            clone = new Material(source);
            string[] keywords = source.shaderKeywords;
            int queue = source.renderQueue;
            clone.shader = target;
            clone.shaderKeywords = keywords;
            clone.renderQueue = queue;
            clone.name = source.name + " (DreamPark occluded)";
            clone.hideFlags = HideFlags.DontSave;
            s_clones[source] = clone;

            if (++s_clonesSincePurge >= 64) PurgeDestroyedSources();
            return clone;
        }

        static Shader TargetFor(Shader shader)
        {
            if (shader == null) return null;
            Shader target;
            if (s_targets.TryGetValue(shader, out target)) return target;
            string name = OccludedShaderNameFor(shader.name);
            target = name != null ? Shader.Find(name) : null;
            if (target != null && !target.isSupported) target = null;
            s_targets[shader] = target;
            return target;
        }

        static void OnSceneUnloaded(Scene scene)
        {
            PurgeDestroyedSources();
            s_rejectedUnderRoot.Clear();
        }

        // A source material from an unloaded bundle is destroyed; drop (and destroy) its clone.
        static void PurgeDestroyedSources()
        {
            s_clonesSincePurge = 0;
            List<Material> dead = null;
            foreach (var kv in s_clones)
                if (kv.Key == null) (dead ?? (dead = new List<Material>())).Add(kv.Key);
            if (dead == null) return;
            foreach (Material key in dead)
            {
                Material clone = s_clones[key];
                s_clones.Remove(key);
                if (clone != null) Object.Destroy(clone);
            }
        }

        static void DestroyClones()
        {
            foreach (var kv in s_clones)
            {
                if (kv.Value == null) continue;
                if (Application.isPlaying) Object.Destroy(kv.Value);
                else Object.DestroyImmediate(kv.Value);
            }
            s_clones.Clear();
        }
    }
}
