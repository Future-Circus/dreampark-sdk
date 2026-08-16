#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.Shaders
{
    // The material inspector for DreamPark-UniversalShader and DreamPark-Unlit.
    // Both graphs point here through the Shader Graph target's Custom Editor GUI
    // field (m_CustomEditorGUI in the .shadergraph). Its whole job is to make
    // DreamParkMaterialRules' invariant visible and unbreakable at the one place a
    // human can break it.
    //
    // WHY THIS IS A WRAPPER AND NOT A SUBCLASS
    //
    // The obvious implementation is `class DreamParkLitShaderGUI : ShaderGraphLitGUI`,
    // overriding DrawSurfaceOptions to draw the Alpha Clipping row disabled. It does
    // not compile: ShaderGraphLitGUI and ShaderGraphUnlitGUI are INTERNAL to
    // Unity.RenderPipelines.Universal.Editor, and you cannot derive from a type you
    // cannot name.
    //
    // The next idea is to derive from BaseShaderGUI, which IS public, and reproduce
    // ShaderGraphLitGUI's forty lines. That does not compile either — the pieces
    // those forty lines are built from (DrawShaderGraphProperties,
    // GetAutomaticQueueControlSetting, the Styles table) are all internal. Getting
    // there means copying a slab of URP's inspector into this repo and re-copying it
    // on every URP bump, to change one row.
    //
    // So: instantiate URP's GUI reflectively, hand it every call through ShaderGUI's
    // public virtual surface, and bracket it. If the reflection ever fails — URP
    // renamed the type, the assembly moved — we fall back to Unity's default property
    // GUI and the material is still editable. A broken inspector must not be able to
    // make a shader unusable.
    //
    // THE COST OF THAT CHOICE, STATED PLAINLY
    //
    // Because we do not own the row, we cannot grey out the Alpha Clipping toggle.
    // What happens instead is that clicking it off snaps it back on within the same
    // repaint, with a HelpBox above saying why. Functionally non-negotiable, visually
    // a rejection rather than a lock. If URP ever makes ShaderGraphLitGUI public,
    // this becomes a two-line override and the toggle can grey out properly.
    public abstract class DreamParkShaderGUIBase : ShaderGUI
    {
        private const string UrpEditorAssembly = "Unity.RenderPipelines.Universal.Editor";

        /// <summary>Fully-qualified name of the URP ShaderGUI to delegate to.</summary>
        protected abstract string InnerTypeName { get; }

        private ShaderGUI inner;
        private bool innerResolved;

        // Unity keeps one ShaderGUI instance alive per open inspector, so resolving
        // once per instance is enough and BaseShaderGUI's first-time-apply state
        // survives between repaints the way it expects.
        private ShaderGUI Inner
        {
            get
            {
                if (innerResolved) return inner;
                innerResolved = true;

                try
                {
                    Type t = Type.GetType(InnerTypeName + ", " + UrpEditorAssembly, throwOnError: false);
                    if (t != null)
                        inner = Activator.CreateInstance(t, nonPublic: true) as ShaderGUI;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        $"[DreamPark] Could not create URP's '{InnerTypeName}' for the material "
                      + $"inspector; falling back to the default property GUI. Surface Options will "
                      + $"not be shown. ({e.GetType().Name}: {e.Message})");
                }

                if (inner == null)
                {
                    Debug.LogWarning(
                        $"[DreamPark] '{InnerTypeName}' was not found in {UrpEditorAssembly}. The "
                      + "DreamPark material inspector is falling back to Unity's default property "
                      + "list. Alpha clipping is still enforced on import and at upload.");
                }

                return inner;
            }
        }

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            // Before: so the toggle the user is about to look at reads the truth,
            // including for a material that arrived misconfigured from a converter or
            // a hand-edited .mat.
            EnforceAll(materialEditor);

            var self = materialEditor.target as Material;
            if (self != null && DreamParkMaterialRules.IsGoverned(self) && DreamParkMaterialRules.IsOpaque(self))
            {
                EditorGUILayout.HelpBox(DreamParkMaterialRules.LockExplanation, MessageType.Info);
                EditorGUILayout.Space(2f);
            }

            var gui = Inner;
            if (gui != null) gui.OnGUI(materialEditor, properties);
            else             base.OnGUI(materialEditor, properties);

            // After: this is the one that actually rejects the click. Enforce is a
            // no-op when the invariant already holds, so a satisfied material is not
            // dirtied on every repaint.
            EnforceAll(materialEditor);
        }

        // ShaderGUI's other two public entry points. ValidateMaterial is what URP's
        // own material postprocessor calls on import, so overriding it means the
        // invariant is restored even for materials whose inspector nobody opens —
        // DreamParkMaterialPostprocessor is the backstop for the paths that do not
        // route through a ShaderGUI at all.
        public override void ValidateMaterial(Material material)
        {
            var gui = Inner;
            if (gui != null) gui.ValidateMaterial(material);
            DreamParkMaterialRules.Enforce(material);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            var gui = Inner;
            if (gui != null) gui.AssignNewShaderToMaterial(material, oldShader, newShader);
            else             base.AssignNewShaderToMaterial(material, oldShader, newShader);

            // Swapping shaders nukes the keyword set (BaseShaderGUI does this on
            // purpose), so this is exactly the moment the invariant goes stale.
            DreamParkMaterialRules.EnforceAndDirty(material);
        }

        private static void EnforceAll(MaterialEditor materialEditor)
        {
            if (materialEditor == null || materialEditor.targets == null) return;

            foreach (var obj in materialEditor.targets)
            {
                var mat = obj as Material;
                if (mat == null) continue;
                DreamParkMaterialRules.EnforceAndDirty(mat);
            }
        }
    }

    /// <summary>Inspector for DreamPark-UniversalShader.</summary>
    public sealed class DreamParkLitShaderGUI : DreamParkShaderGUIBase
    {
        protected override string InnerTypeName { get { return "UnityEditor.ShaderGraphLitGUI"; } }
    }

    /// <summary>Inspector for DreamPark-Unlit.</summary>
    public sealed class DreamParkUnlitShaderGUI : DreamParkShaderGUIBase
    {
        protected override string InnerTypeName { get { return "UnityEditor.ShaderGraphUnlitGUI"; } }
    }
}
#endif
