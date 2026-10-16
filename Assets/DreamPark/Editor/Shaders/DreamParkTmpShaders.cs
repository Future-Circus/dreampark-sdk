#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.Shaders
{
    // TextMesh Pro materials and Meta environment occlusion (Oct 2026).
    //
    // TMP's stock SDF shaders ("TextMeshPro/Distance Field", "TextMeshPro/Mobile/Distance
    // Field") have no Depth API integration, so world-space text draws over the guest's
    // hands, furniture and walls on a Quest. "DreamPark/TextMeshPro/Distance Field" and
    // "DreamPark/TextMeshPro/Mobile/Distance Field" are the same shaders with the Meta
    // occlusion macros added: same property names, same TMP inspector, same look when the
    // occlusion keywords are off. Re-pointing a material between the two is therefore
    // lossless: it changes the shader reference and nothing else (textures, colours,
    // outline/underlay keywords and render queue are kept, GUIDs untouched).
    //
    // Three paths use this:
    //   • the SDK's own default TMP materials ship re-pointed (Assets/DreamPark/ThirdParty/
    //     TextMesh Pro/Resources/Fonts & Materials/, Assets/DreamPark/Fonts/OpenSans.asset);
    //   • MetaOcclusionCheck reports a creator's TMP material still on a stock SDF shader as
    //     a WARNING with a one-click "Use DreamPark TMP shader" repair;
    //   • the menu sweep below repoints every creator TMP material at once.
    // At runtime DreamParkTmpOcclusion also swaps world-space content text that shipped on a
    // stock shader (new app only), so the warning is about older apps and about the asset
    // being right as shipped.
    //
    // Deliberately NOT an import-time postprocessor: a font asset's material is shared by
    // world-space text and screen-space UI, dynamic font assets re-save constantly, and
    // silently rewriting creator assets on every import is the wrong default for a
    // non-blocking issue the runtime already covers. The check and the menu are explicit.
    //
    // Names are resolved with Shader.Find, never by GUID: dreampark-core and dreampark-sdk
    // carry different .meta GUIDs for TMP's shaders and fonts (historical; never change them).
    public static class DreamParkTmpShaders
    {
        private const string SdkPrefix = "Assets/DreamPark/";

        /// <summary>True when the shader is a stock TMP SDF shader with a DreamPark occluded twin.</summary>
        public static bool IsStockTmpSdf(Shader shader)
        {
            return shader != null && DreamParkTmpOcclusion.OccludedShaderNameFor(shader.name) != null;
        }

        /// <summary>The DreamPark occluded twin of a stock TMP SDF shader, or null.</summary>
        public static Shader OccludedFor(Shader shader)
        {
            if (shader == null) return null;
            string name = DreamParkTmpOcclusion.OccludedShaderNameFor(shader.name);
            return string.IsNullOrEmpty(name) ? null : Shader.Find(name);
        }

        /// <summary>
        /// Re-points a material from a stock TMP SDF shader to its DreamPark twin, keeping
        /// properties, keywords and render queue. Returns true when it changed the material.
        /// Saves the asset file when <paramref name="save"/> (a font asset's material is a
        /// sub-asset, so the whole .asset is written).
        /// </summary>
        public static bool Repoint(Material mat, bool save = true)
        {
            if (mat == null) return false;
            Shader target = OccludedFor(mat.shader);
            if (target == null) return false;

            string path = AssetDatabase.GetAssetPath(mat);
            if (!string.IsNullOrEmpty(path) && !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return false;   // Packages/ are immutable

            string[] keywords = mat.shaderKeywords;
            int rawQueue = mat.rawRenderQueue;

            Undo.RecordObject(mat, "Use DreamPark TMP shader");
            mat.shader = target;
            mat.shaderKeywords = keywords;
            mat.renderQueue = rawQueue;
            EditorUtility.SetDirty(mat);

            if (save && !string.IsNullOrEmpty(path))
                AssetDatabase.SaveAssetIfDirty(new GUID(AssetDatabase.AssetPathToGUID(path)));
            return mat.shader == target;
        }

        /// <summary>
        /// Every material under Assets/ (outside Assets/DreamPark/ and ThirdPartyLocal/) that is
        /// on a stock TMP SDF shader — .mat files and materials inside TMP font assets.
        /// </summary>
        public static List<Material> FindStockTmpMaterials()
        {
            var found = new List<Material>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" })
                         .Concat(AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Assets" })))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                if (path.StartsWith(SdkPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!(path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                      || path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))) continue;

                foreach (var m in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
                    if (m != null && IsStockTmpSdf(m.shader)) found.Add(m);
            }
            return found;
        }

        [MenuItem("DreamPark/Troubleshooting/Use Occluded TextMesh Pro Shaders")]
        private static void RepointAllMenu()
        {
            List<Material> mats = FindStockTmpMaterials();
            if (mats.Count == 0)
            {
                EditorUtility.DisplayDialog("TextMesh Pro occlusion",
                    "No TextMesh Pro material under Assets/ (outside Assets/DreamPark/) is on a stock "
                  + "SDF shader. Nothing to do.", "OK");
                return;
            }

            string list = string.Join("\n", mats.Take(12).Select(m => "• " + AssetDatabase.GetAssetPath(m) + " (" + m.name + ")"));
            if (mats.Count > 12) list += "\n… and " + (mats.Count - 12) + " more";
            if (!EditorUtility.DisplayDialog("TextMesh Pro occlusion",
                    mats.Count + " TextMesh Pro material(s) are on a stock SDF shader, which has no Meta "
                  + "environment occlusion (text draws over the guest's hands and furniture on Quest).\n\n"
                  + list + "\n\nRe-point them to the DreamPark TMP shaders? Same properties and look; "
                  + "only the shader reference changes. Use version control to revert.",
                    "Re-point", "Cancel"))
                return;

            int changed = 0;
            foreach (var m in mats) if (Repoint(m, save: false)) changed++;
            AssetDatabase.SaveAssets();
            Debug.Log($"[DreamPark] Re-pointed {changed}/{mats.Count} TextMesh Pro material(s) to the DreamPark occluded TMP shaders.");
        }
    }
}
#endif
