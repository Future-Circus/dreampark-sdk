#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.Shaders
{
    /// <summary>
    /// Keeps a material's shader keywords and the toggle floats that own them in agreement.
    ///
    /// A property declared <c>[Toggle(_KEYWORD)] _Prop</c> (or <c>[KeywordEnum(A, B)] _Prop</c>)
    /// is the source of truth for that keyword whenever Unity re-validates the material: the
    /// inspector's toggle drawer re-applies the keyword from the float. A converter that calls
    /// <c>EnableKeyword</c> without also setting the float therefore produces a material that
    /// renders correctly until the next validation, then silently loses the feature. That is
    /// what happened to WizardsWay's converted particles (Oct 2026): soft particles, noise,
    /// alpha test and emission were enabled as keywords with their toggles left at 0.
    ///
    /// The bindings are read from the shader's own property attributes, so this covers every
    /// shader the converter writes (DreamPark/Particles, and the Shader Graph boolean keywords
    /// of DreamPark-UniversalShader / DreamPark-Unlit) without a hand-kept table.
    /// Package-free and not behind !DREAMPARKCORE: MaterialConverter uses it in core too.
    /// </summary>
    public static class DreamParkKeywordToggles
    {
        public const string ParticleShaderName = "DreamPark/Particles";

        /// <summary>[Toggle(KW)] / [Toggle] / [ToggleOff(KW)] on a float property.</summary>
        public struct ToggleBinding
        {
            public string property;
            public string keyword;
            public bool inverted;   // ToggleOff: keyword is ON when the float is 0
        }

        /// <summary>[KeywordEnum(A, B, …)] on a float property: keyword i ⇔ float == i.</summary>
        public struct EnumBinding
        {
            public string property;
            public string[] keywords;
        }

        public static void Describe(Shader shader, List<ToggleBinding> toggles, List<EnumBinding> enums)
        {
            toggles?.Clear();
            enums?.Clear();
            if (shader == null) return;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var type = shader.GetPropertyType(i);
                if (type != UnityEngine.Rendering.ShaderPropertyType.Float
                    && type != UnityEngine.Rendering.ShaderPropertyType.Range
                    && type != UnityEngine.Rendering.ShaderPropertyType.Int) continue;
                string property = shader.GetPropertyName(i);
                string[] attributes = shader.GetPropertyAttributes(i);
                if (attributes == null) continue;
                foreach (string raw in attributes)
                {
                    if (!TryParse(raw, out string name, out string args)) continue;
                    if (name == "Toggle" || name == "MaterialToggle")
                    {
                        toggles?.Add(new ToggleBinding
                        {
                            property = property,
                            keyword = string.IsNullOrEmpty(args) ? property.ToUpperInvariant() + "_ON" : args,
                        });
                    }
                    else if (name == "ToggleOff")
                    {
                        toggles?.Add(new ToggleBinding
                        {
                            property = property,
                            keyword = string.IsNullOrEmpty(args) ? property.ToUpperInvariant() + "_OFF" : args,
                            inverted = true,
                        });
                    }
                    else if (name == "KeywordEnum" && !string.IsNullOrEmpty(args))
                    {
                        string[] values = args.Split(',');
                        var keywords = new string[values.Length];
                        for (int v = 0; v < values.Length; v++)
                            keywords[v] = (property + "_" + values[v].Trim()).Replace(' ', '_').ToUpperInvariant();
                        enums?.Add(new EnumBinding { property = property, keywords = keywords });
                    }
                }
            }
        }

        // "Toggle(_SOFTPARTICLES_ON)" → ("Toggle", "_SOFTPARTICLES_ON"); "Toggle" → ("Toggle", "").
        private static bool TryParse(string raw, out string name, out string args)
        {
            name = null;
            args = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string text = raw.Trim();
            int open = text.IndexOf('(');
            if (open < 0) { name = text; return true; }
            int close = text.LastIndexOf(')');
            if (close < open) return false;
            name = text.Substring(0, open).Trim();
            args = text.Substring(open + 1, close - open - 1).Trim();
            return true;
        }

        /// <summary>
        /// Enable or disable a keyword AND set the toggle float that owns it, so the next
        /// validation keeps it. Keywords no property owns are just set.
        /// </summary>
        public static void SetKeyword(Material material, string keyword, bool on)
        {
            if (material == null || string.IsNullOrEmpty(keyword)) return;
            if (on) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);

            var toggles = new List<ToggleBinding>();
            Describe(material.shader, toggles, null);
            foreach (ToggleBinding binding in toggles)
            {
                if (!string.Equals(binding.keyword, keyword, StringComparison.Ordinal)) continue;
                if (!material.HasProperty(binding.property)) continue;
                material.SetFloat(binding.property, on != binding.inverted ? 1f : 0f);
            }
        }

        /// <summary>
        /// Every place a material's toggle floats disagree with its keywords, as
        /// "property=value but KEYWORD is on|off" lines. Empty when they agree.
        /// </summary>
        public static List<string> FindMismatches(Material material)
        {
            var result = new List<string>();
            Visit(material, apply: false, result);
            return result;
        }

        /// <summary>
        /// Set every toggle float to match its keyword (the keyword wins: it is what the
        /// material renders with right now, and what the converter meant). Returns the
        /// changes made, or that would be made when apply is false.
        /// </summary>
        public static List<string> SyncTogglesToKeywords(Material material, bool apply)
        {
            var result = new List<string>();
            Visit(material, apply, result);
            return result;
        }

        private static void Visit(Material material, bool apply, List<string> changes) =>
            Visit(material, apply, changes, null, null);

        // keywords/floats: the state to judge (the material's serialized file when given);
        // null reads the live material.
        private static void Visit(Material material, bool apply, List<string> changes,
            HashSet<string> keywords, Dictionary<string, float> floats)
        {
            if (material == null || material.shader == null) return;
            var toggles = new List<ToggleBinding>();
            var enums = new List<EnumBinding>();
            Describe(material.shader, toggles, enums);
            Func<string, bool> keywordOnOf = keyword => keywords != null
                ? keywords.Contains(keyword)
                : material.IsKeywordEnabled(keyword);
            Func<string, float> floatOf = property => floats != null && floats.TryGetValue(property, out float value)
                ? value
                : material.GetFloat(property);

            foreach (ToggleBinding binding in toggles)
            {
                if (!material.HasProperty(binding.property)) continue;
                bool keywordOn = keywordOnOf(binding.keyword);
                bool floatOn = floatOf(binding.property) > 0.5f;
                bool expectedFloatOn = keywordOn != binding.inverted;
                if (floatOn == expectedFloatOn) continue;
                changes.Add($"{binding.property} {(floatOn ? 1 : 0)} -> {(expectedFloatOn ? 1 : 0)} ({binding.keyword} is {(keywordOn ? "on" : "off")})");
                if (!apply) continue;
                material.SetFloat(binding.property, expectedFloatOn ? 1f : 0f);
                if (keywordOn) material.EnableKeyword(binding.keyword);
                else material.DisableKeyword(binding.keyword);
            }

            foreach (EnumBinding binding in enums)
            {
                if (!material.HasProperty(binding.property)) continue;
                int enabled = -1;
                int enabledCount = 0;
                for (int k = 0; k < binding.keywords.Length; k++)
                {
                    if (!keywordOnOf(binding.keywords[k])) continue;
                    enabled = k;
                    enabledCount++;
                }
                // None or several on: no single keyword to follow, leave the float alone.
                if (enabledCount != 1) continue;
                int current = Mathf.RoundToInt(floatOf(binding.property));
                if (current == enabled) continue;
                changes.Add($"{binding.property} {current} -> {enabled} ({binding.keywords[enabled]} is on)");
                if (!apply) continue;
                material.SetFloat(binding.property, enabled);
                for (int k = 0; k < binding.keywords.Length; k++)
                {
                    if (k == enabled) material.EnableKeyword(binding.keywords[k]);
                    else material.DisableKeyword(binding.keywords[k]);
                }
            }
        }

        // ------------------------------------------------------------------ project repair

        public sealed class RepairReport
        {
            public int scanned;
            public readonly List<string> lines = new List<string>();
            public readonly List<string> materials = new List<string>();
        }

        /// <summary>
        /// Scan DreamPark/Particles materials under the given folders (default Assets/) and,
        /// when apply is true, set their toggles to match their keywords and save.
        /// </summary>
        public static RepairReport RepairParticleToggles(bool apply, string[] folders = null)
        {
            var report = new RepairReport();
            string[] guids = AssetDatabase.FindAssets("t:Material", folders ?? new[] { "Assets" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null || material.shader == null || material.shader.name != ParticleShaderName) continue;
                report.scanned++;
                // Judge what is on disk. The Editor re-derives toggle keywords from their floats
                // when it loads or touches a material, so the live object can already have lost
                // the keyword the converter wrote; the file still has it.
                var changes = new List<string>();
                if (TryReadSerializedState(path, out HashSet<string> keywords, out Dictionary<string, float> floats))
                    Visit(material, apply, changes, keywords, floats);
                else
                    Visit(material, apply, changes);
                if (changes.Count == 0) continue;
                report.materials.Add(path);
                report.lines.Add(path + ": " + string.Join("; ", changes));
                if (apply) EditorUtility.SetDirty(material);
            }
            if (apply && report.materials.Count > 0) AssetDatabase.SaveAssets();
            return report;
        }

        /// <summary>
        /// The keywords (m_ValidKeywords, or the older single-line m_ShaderKeywords) and
        /// m_Floats of a text-serialized .mat. False for binary or unreadable files.
        /// </summary>
        internal static bool TryReadSerializedState(string assetPath,
            out HashSet<string> keywords, out Dictionary<string, float> floats)
        {
            keywords = new HashSet<string>(StringComparer.Ordinal);
            floats = new Dictionary<string, float>(StringComparer.Ordinal);
            string[] lines;
            try
            {
                if (string.IsNullOrEmpty(assetPath) || !System.IO.File.Exists(assetPath)) return false;
                lines = System.IO.File.ReadAllLines(assetPath);
            }
            catch (Exception)
            {
                return false;
            }
            if (lines.Length == 0 || !lines[0].StartsWith("%YAML", StringComparison.Ordinal)) return false;

            string section = null;
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (line.StartsWith("  m_ValidKeywords:", StringComparison.Ordinal)) { section = "kw"; continue; }
                if (line.StartsWith("  m_ShaderKeywords:", StringComparison.Ordinal))
                {
                    string value = trimmed.Substring("m_ShaderKeywords:".Length).Trim();
                    foreach (string keyword in value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        keywords.Add(keyword);
                    section = null;
                    continue;
                }
                if (line.StartsWith("    m_Floats:", StringComparison.Ordinal)) { section = "floats"; continue; }
                if (section == "kw")
                {
                    if (line.StartsWith("  - ", StringComparison.Ordinal)) { keywords.Add(trimmed.Substring(2).Trim()); continue; }
                    section = null;
                }
                else if (section == "floats")
                {
                    if (!line.StartsWith("    - ", StringComparison.Ordinal)) { section = null; continue; }
                    string entry = trimmed.Substring(2);
                    int colon = entry.IndexOf(':');
                    if (colon <= 0) continue;
                    if (float.TryParse(entry.Substring(colon + 1).Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                        floats[entry.Substring(0, colon).Trim()] = parsed;
                }
            }
            return true;
        }

        [MenuItem("DreamPark/Troubleshooting/Fix Particle Material Keyword Toggles", false, 215)]
        private static void RepairMenu() => RunMenu(apply: true);

        [MenuItem("DreamPark/Troubleshooting/Report Particle Material Keyword Toggles", false, 216)]
        private static void ReportMenu() => RunMenu(apply: false);

        private static void RunMenu(bool apply)
        {
            RepairReport report = RepairParticleToggles(apply);
            string verb = apply ? "Fixed" : "Would fix";
            string summary = report.materials.Count == 0
                ? $"All {report.scanned} DreamPark/Particles materials have toggles that match their keywords."
                : $"{verb} {report.materials.Count} of {report.scanned} DreamPark/Particles materials (toggles set to match their keywords).";
            Debug.Log("[DreamPark] Particle keyword toggles: " + summary
                + (report.lines.Count > 0 ? "\n" + string.Join("\n", report.lines) : ""));
            if (DreamParkAutomation.IsActive) return;
            string body = summary
                + (report.materials.Count > 0
                    ? "\n\n" + string.Join("\n", report.materials.GetRange(0, Mathf.Min(15, report.materials.Count)))
                      + (report.materials.Count > 15 ? $"\n…and {report.materials.Count - 15} more (see the Console)." : "")
                    : "")
                + (apply && report.materials.Count > 0 ? "\n\nThis cannot be undone with Ctrl-Z. Use version control to review the diff." : "");
            EditorUtility.DisplayDialog("Particle Material Keyword Toggles", body, "OK");
        }
    }
}
#endif
