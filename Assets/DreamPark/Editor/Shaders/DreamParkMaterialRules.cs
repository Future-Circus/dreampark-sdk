#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using UnityEditor;
using UnityEngine;
using DreamPark.EditorTools.MaterialConversion;

namespace DreamPark.EditorTools.Shaders
{
    // The one place that knows what "correctly configured" means for a material on a
    // DreamPark Shader Graph shader. Everything else — the custom ShaderGUI, the
    // asset postprocessor, the MaterialConverter, the pre-upload gate — asks this
    // class rather than reimplementing the rule.
    //
    // THE RULE
    //
    //   Surface Type = Opaque  ⇒  Alpha Clipping is ON, with a threshold above zero.
    //                             Not a default, not a suggestion: an invariant that
    //                             is re-established every time anyone looks at the
    //                             material.
    //   Surface Type = Transparent ⇒ Alpha Clipping is the author's call — but the
    //                             float and the keyword still have to agree.
    //
    // WHY
    //
    // Meta's environment-depth occlusion does not remove pixels by itself. The
    // OcclusionSubGraph drives the occlusion result into the fragment's ALPHA — that
    // is the entire integration, and it is what MetaOcclusionCheck verifies is
    // present in the shader. Both DreamPark graphs multiply it in:
    //
    //   DreamPark-UniversalShader:  Alpha = baseTex.a * (_opacity     * Occlusion)
    //   DreamPark-Unlit:            Alpha = baseTex.a *  Occlusion    * _baseColor.a
    //
    // A TRANSPARENT surface consumes that alpha through the blend, so occlusion just
    // works. An OPAQUE surface discards alpha entirely unless alpha clipping is
    // enabled, because there is nothing downstream that reads it. So an opaque
    // DreamPark material with clipping off has the occlusion wiring, passes
    // MetaOcclusionCheck, renders perfectly in the Editor — and draws straight
    // through the guest's hands on a headset.
    //
    // That is the worst possible shape for a bug: invisible everywhere except the
    // one place it matters, and unfixable after upload. Hence the belt and braces.
    //
    // THREE PIECES OF STATE, AND THEY CAN ALL DISAGREE
    //
    //   _AlphaClip (float)   — what the inspector draws, what the ShaderGUI reads.
    //   _ALPHATEST_ON        — the keyword that selects the clipping variant.
    //   _alphaClipping       — the threshold. Zero clips nothing, so clipping can be
    //                          "on" in both of the above and still discard occlusion.
    //
    // Assigning a new shader to a material (`material.shader = x`) carries over
    // same-named float properties AND the existing keyword set, so a material
    // converted from a non-clipping source lands with _AlphaClip = whatever the
    // source had and _ALPHATEST_ON off. If the source had no _AlphaClip at all it
    // picks up the graph default of 1 while the keyword stays off — the inspector
    // then shows the toggle ON while the shader compiles the non-clipping variant.
    // A check that reads only one of the three is wrong more often than it is right.
    // IsAlphaClipOn() therefore requires all three, always.
    //
    // WHY WE DELEGATE THE REPAIR TO URP
    //
    // Turning clipping on is not one write. It is the keyword, _AlphaToMask, the
    // RenderType override tag, and the render queue moving from Geometry (2000) to
    // AlphaTest (2450). BaseShaderGUI.SetupMaterialBlendMode is public URP API and
    // does exactly that set, correctly, for the URP version this project is pinned
    // to. Reimplementing it here would fork URP internals and drift on the next
    // package bump.
    //
    // NOTHING HERE MAY REPORT A CHANGE IT DID NOT MAKE
    //
    // Enforce()'s bool return is load-bearing: DreamParkMaterialPostprocessor saves
    // on it, which re-imports, which calls Enforce again. A `true` that does not
    // correspond to the invariant actually becoming satisfied is an unbounded
    // reimport loop with a Debug.Log per cycle. Every early-out below exists for that
    // reason, and the keyword is re-read AFTER SyncSurfaceState rather than assumed.
    public static class DreamParkMaterialRules
    {
        // URP's shared property names. Literals rather than
        // UnityEditor.Rendering.Universal.ShaderGraph.Property because that type is
        // internal to the URP editor assembly.
        public const string SurfaceProp   = "_Surface";       // 0 = Opaque, 1 = Transparent
        public const string AlphaClipProp = "_AlphaClip";     // 0 / 1 float toggle
        public const string QueueCtrlProp = "_QueueControl";  // 0 = Auto, 1 = UserOverride
        public const string AlphaTestKeyword = "_ALPHATEST_ON";

        // NOT "_Cutoff". URP's stock Lit shader calls the clip threshold _Cutoff, but
        // a Shader Graph does not get one for free — the threshold comes from whatever
        // is wired into the Alpha Clip Threshold block. In DreamPark-UniversalShader
        // that is an exposed Range(0,1) property named `alphaClipping`, reference name
        // `_alphaClipping`, default 0.5. In DreamPark-Unlit the block is an unexposed
        // constant 0.5, so the property does not exist there at all.
        //
        // Every use is guarded by HasProperty, so the Unlit case is a no-op rather
        // than a silent write to nothing. If a future graph exposes its own threshold
        // under a different name, add it here rather than at the call sites.
        public const string ThresholdProp = "_alphaClipping";

        // Matches the graphs' own default. A material that arrives with a threshold of
        // 0 clips nothing, which is a silent no-op dressed up as a fix.
        public const float DefaultThreshold = 0.5f;

        // ------------------------------------------------------------------
        // Scope

        // Only the two Shader Graph shaders. DreamPark/Particles is a hand-written
        // shader with its own _ALPHATEST_ON handling and its own conversion path in
        // MaterialConverter (ConvertParticleMaterial, which already reads and writes
        // the keyword deliberately) — reaching into it from here would fight that
        // code rather than help it. Particles are also transparent by construction,
        // so the rule would never fire anyway.
        public static bool IsGoverned(Material mat)
        {
            if (mat == null || mat.shader == null) return false;
            string n = mat.shader.name;
            return n == DreamParkShaderNames.Universal
                || n == DreamParkShaderNames.Unlit;
        }

        public static bool IsOpaque(Material mat)
        {
            if (mat == null) return false;
            // No _Surface at all means the graph does not expose material override
            // for surface type, i.e. it is baked to whatever the graph says. Both
            // DreamPark graphs do expose it; treating "absent" as not-opaque keeps a
            // future graph that turns Allow Material Override off from being told it
            // is misconfigured about a property it does not have.
            if (!mat.HasProperty(SurfaceProp)) return false;
            return mat.GetFloat(SurfaceProp) < 0.5f;
        }

        /// <summary>
        /// All three pieces of state agree that this material clips. See the header:
        /// any two of them can be right while the third silently defeats occlusion.
        /// </summary>
        public static bool IsAlphaClipOn(Material mat)
        {
            if (mat == null) return false;

            bool prop    = mat.HasProperty(AlphaClipProp) && mat.GetFloat(AlphaClipProp) >= 0.5f;
            bool keyword = mat.IsKeywordEnabled(AlphaTestKeyword);

            // Absent threshold = the graph hard-codes one (DreamPark-Unlit's is a
            // constant 0.5), which is fine. Present-and-zero clips nothing.
            bool threshold = !mat.HasProperty(ThresholdProp) || mat.GetFloat(ThresholdProp) > 0f;

            return prop && keyword && threshold;
        }

        /// <summary>
        /// True when this material is one we govern, is opaque, and is genuinely
        /// clipping. Materials we do not govern return true — "nothing to answer for"
        /// — so callers can use this as a plain pass/fail without special-casing.
        /// Callers that need to distinguish "fine" from "not ours" should pair it with
        /// IsGoverned.
        /// </summary>
        public static bool IsSatisfied(Material mat)
        {
            if (!IsGoverned(mat)) return true;
            if (!IsOpaque(mat)) return true;
            return IsAlphaClipOn(mat);
        }

        /// <summary>
        /// The inverse, named for the reader: this material is opaque, on a DreamPark
        /// graph shader, and will not be occluded on a headset.
        /// </summary>
        public static bool IsBrokenForOcclusion(Material mat)
        {
            return !IsSatisfied(mat);
        }

        // ------------------------------------------------------------------
        // Repair

        /// <summary>
        /// Re-establishes the invariant. Returns true if the material actually changed
        /// — see the header on why that must never be optimistic.
        ///
        /// Does NOT call SetDirty or SaveAssets. The caller owns that, because the
        /// right answer differs between an import callback, an inspector repaint and
        /// a batch conversion.
        /// </summary>
        public static bool Enforce(Material mat)
        {
            if (!IsGoverned(mat)) return false;

            // If the graph does not expose the toggle there is nothing here we can
            // drive, and every write below would be a no-op that still reported
            // success — the loop described in the header.
            if (!mat.HasProperty(AlphaClipProp)) return false;

            bool keywordBefore = mat.IsKeywordEnabled(AlphaTestKeyword);
            bool clipProp      = mat.GetFloat(AlphaClipProp) >= 0.5f;

            // ── Transparent ────────────────────────────────────────────────
            // The author's call, and occlusion works either way. But a shader swap
            // drags the old keyword set across, so the float and the keyword can
            // disagree — a material showing "Alpha Clipping: off" that is in fact
            // clipping at 0.5 and eating the edges of a soft texture. Resync only.
            if (!IsOpaque(mat))
            {
                if (clipProp == keywordBefore) return false;
                SyncSurfaceState(mat);
                return mat.IsKeywordEnabled(AlphaTestKeyword) != keywordBefore;
            }

            // ── Opaque ─────────────────────────────────────────────────────
            if (IsAlphaClipOn(mat)) return false;

            bool changed = false;

            if (!clipProp)
            {
                mat.SetFloat(AlphaClipProp, 1f);
                changed = true;
            }

            // A zero threshold clips nothing, so turning clipping on would look like a
            // fix and change nothing on the headset. Only rescue a genuinely unset
            // value — an author who chose 0.1 keeps 0.1.
            if (mat.HasProperty(ThresholdProp) && mat.GetFloat(ThresholdProp) <= 0f)
            {
                mat.SetFloat(ThresholdProp, DefaultThreshold);
                changed = true;
            }

            changed |= RescueDeadAlphaInputs(mat);

            SyncSurfaceState(mat);

            // Re-read rather than assume. If URP declined to enable the keyword for a
            // reason we did not anticipate, this reports false and the postprocessor
            // stops rather than saving in a circle.
            changed |= mat.IsKeywordEnabled(AlphaTestKeyword) != keywordBefore;

            return changed;
        }

        // Both graphs build Alpha as a product:
        //
        //   Universal:  Alpha = baseTex.a * (_opacity      * Occlusion)
        //   Unlit:      Alpha = baseTex.a *  Occlusion     * _baseColor.a
        //
        // On an OPAQUE surface those scalars are dead weight — nothing downstream
        // reads alpha, so they have no visible effect and nobody has any reason to
        // have kept them meaningful. The moment we turn clipping on they become live,
        // and a material sitting at _opacity = 0.3 goes from "looks fine" to
        // "completely invisible" as a direct result of our repair.
        //
        // So we neutralise them in the same breath, UNCONDITIONALLY rather than only
        // when they fall below the threshold. The threshold test is not sufficient:
        // the scalar is multiplied by the texture's alpha, so _opacity = 0.6 against
        // an albedo alpha of 0.8 gives 0.48 and clips everything, while 0.6 on its own
        // looks safe. Since the value is provably dead on an opaque surface, setting
        // it to 1 preserves exactly what the material looks like right now — which is
        // the whole point: enabling clipping should fix occlusion and change nothing
        // else.
        //
        // Only ever reached on the path where clipping is being turned ON. Once a
        // material is legitimately clipping these scalars are the author's, and we
        // keep our hands off them.
        private static bool RescueDeadAlphaInputs(Material mat)
        {
            bool changed = false;

            // DreamPark-UniversalShader
            if (mat.HasProperty("_opacity") && mat.GetFloat("_opacity") < 1f)
            {
                mat.SetFloat("_opacity", 1f);
                changed = true;
            }

            // DreamPark-Unlit. Gated on the shader name because _baseColor exists on
            // BOTH graphs but only feeds Alpha on the Unlit one — on Universal its
            // alpha channel is genuinely unused, and rewriting it there would dirty
            // the material to no effect and show up as noise in every diff.
            if (mat.shader != null
                && mat.shader.name == DreamParkShaderNames.Unlit
                && mat.HasProperty("_baseColor"))
            {
                Color c = mat.GetColor("_baseColor");
                if (c.a < 1f)
                {
                    c.a = 1f;
                    mat.SetColor("_baseColor", c);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Pushes _Surface / _AlphaClip / _Blend through URP's own surface-option
        /// sync, which owns the keyword, _AlphaToMask, the RenderType tag and the
        /// automatic render queue. Safe to call on any URP material, and idempotent.
        ///
        /// Wrapped because this is the one call here that reaches into another
        /// package: if a future URP moves or renames it we fall back to setting the
        /// keyword by hand rather than throwing inside an import callback, where an
        /// exception would be reported against whatever asset happened to be next.
        ///
        /// ONE SHARP EDGE, HANDLED HERE. BaseShaderGUI exposes two ways in.
        /// UpdateMaterialSurfaceOptions(material, automaticRenderQueue) honours the
        /// material's Queue Control setting — but it is internal. The public
        /// SetupMaterialBlendMode does not: it assigns the computed automatic queue
        /// unconditionally, so calling it on a material whose author set Queue Control
        /// to "User Override" silently discards their custom render queue. Since we
        /// call this from an import callback, that would be a data-loss bug nobody
        /// would connect to alpha clipping. So we read the queue and the control mode
        /// first and put a user-owned queue back afterwards.
        /// </summary>
        public static void SyncSurfaceState(Material mat)
        {
            if (mat == null) return;

            bool userOwnsQueue = mat.HasProperty(QueueCtrlProp)
                              && mat.GetFloat(QueueCtrlProp) >= 0.5f;   // 1 = UserOverride
            int queueBefore = mat.renderQueue;

            try
            {
                UnityEditor.BaseShaderGUI.SetupMaterialBlendMode(mat);

                if (userOwnsQueue && mat.renderQueue != queueBefore)
                    mat.renderQueue = queueBefore;
            }
            catch (Exception e)
            {
                bool clip = mat.HasProperty(AlphaClipProp) && mat.GetFloat(AlphaClipProp) >= 0.5f;
                if (clip) mat.EnableKeyword(AlphaTestKeyword);
                else      mat.DisableKeyword(AlphaTestKeyword);

                Debug.LogWarning(
                    "[DreamPark] URP's SetupMaterialBlendMode was unavailable; fell back to setting "
                  + $"{AlphaTestKeyword} directly on '{mat.name}'. Render queue and _AlphaToMask may "
                  + $"be stale — reopen the material inspector to resync. ({e.GetType().Name}: {e.Message})");
            }
        }

        /// <summary>
        /// Enforce plus the asset bookkeeping, for callers that are editing a saved
        /// material asset. Returns true if the asset was modified.
        /// </summary>
        public static bool EnforceAndDirty(Material mat)
        {
            if (!Enforce(mat)) return false;
            EditorUtility.SetDirty(mat);
            return true;
        }

        // ------------------------------------------------------------------
        // Shared copy, so the inspector notice, the converter log and the pre-upload
        // finding all say the same thing.

        public const string LockExplanation =
            "Alpha Clipping is required while Surface Type is Opaque. Meta's environment "
          + "occlusion writes its result into alpha, and an opaque surface discards alpha "
          + "unless it is clipped — without this the object renders over the guest's hands, "
          + "furniture and walls on a headset. Switch Surface Type to Transparent if you "
          + "need soft blending; occlusion works there without clipping.";
    }
}
#endif
