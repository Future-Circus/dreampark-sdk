#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.MaterialConversion
{
    /// <summary>
    /// Popup window that shows the full ParticleDiffReport for one material.
    ///
    /// Layout (top to bottom):
    ///   - Header: material path, source shader, target shader, top severity badge
    ///   - Toggle: "Show low / ignored entries" (off by default — focus the
    ///     user's attention on the things that actually matter)
    ///   - Critical / High / Medium / Low sections, each foldable
    ///   - Mapped section at the bottom (collapsed by default) so the user can
    ///     verify what WILL carry over
    /// </summary>
    public class ParticleDiffPopup : EditorWindow
    {
        private string _materialPath;
        private Material _material;
        private ParticleDiffReport _report;
        private Vector2 _scroll;
        private bool _showLow = false;
        private bool _showIgnored = false;
        private bool _showMapped = false;

        public static void Open(Material mat, string assetPath, ParticleDiffReport report)
        {
            var w = CreateInstance<ParticleDiffPopup>();
            w._material = mat;
            w._materialPath = assetPath;
            w._report = report;
            w.titleContent = new GUIContent($"Particle Diff — {System.IO.Path.GetFileName(assetPath)}");
            w.minSize = new Vector2(720, 480);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            if (_report == null)
            {
                EditorGUILayout.LabelField("No report to display.");
                return;
            }

            DrawHeader();
            EditorGUILayout.Space(6);
            DrawExplanation();
            EditorGUILayout.Space(4);
            DrawFilters();
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // Action-oriented sections. We collapse the four-level severity
            // scale into two user-visible buckets that answer concrete
            // questions: "what will break" and "what will look different".
            // The internal severity grades still drive ordering inside
            // each bucket (Critical before High before Medium) so the most
            // impactful items show first.
            DrawIssueSection(
                "Won't carry over (visible breakage)",
                new[] { DiffSeverity.Critical },
                new Color(0.85f, 0.30f, 0.30f, 0.20f));

            DrawIssueSection(
                "Will look different (data lost in translation)",
                new[] { DiffSeverity.High, DiffSeverity.Medium },
                new Color(0.95f, 0.65f, 0.30f, 0.18f));

            DrawIssueSection(
                "Approximated (vendor feature → simpler DreamPark equivalent)",
                new[] { DiffSeverity.Approximated },
                new Color(0.45f, 0.65f, 0.95f, 0.18f));

            if (_showLow)
                DrawIssueSection(
                    "Negligible (default values without target slots)",
                    new[] { DiffSeverity.Low },
                    new Color(0.55f, 0.80f, 0.55f, 0.14f));
            if (_showIgnored)
                DrawIssueSection(
                    "Engine metadata (not visual data)",
                    new[] { DiffSeverity.Ignored },
                    new Color(0.6f, 0.6f, 0.6f, 0.10f));

            EditorGUILayout.Space(8);
            DrawMappedSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Particle Conversion Diff", EditorStyles.boldLabel, GUILayout.MinWidth(220));
                    GUILayout.FlexibleSpace();
                    DrawReadinessBadge(_report.Readiness, _report.Headline);
                }
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Material:", _materialPath, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Source shader:", _report.sourceShaderName, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Target shader:", _report.targetShaderName, EditorStyles.miniLabel);

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Ping material", GUILayout.Width(110)))
                    {
                        if (_material != null) EditorGUIUtility.PingObject(_material);
                    }
                    if (GUILayout.Button("Copy report to clipboard", GUILayout.Width(190)))
                    {
                        EditorGUIUtility.systemCopyBuffer = BuildPlainTextReport();
                    }
                }
            }
        }

        // Plain-language summary of the readiness state, right below the
        // header. This is the "what should I do with this material?" line
        // — three sentences that map 1:1 to the three Readiness states.
        private void DrawExplanation()
        {
            string msg;
            MessageType type;
            switch (_report.Readiness)
            {
                case ConversionReadiness.Blocked:
                    msg = "Don't convert this material as-is. The source uses textures or features that "
                        + "DreamPark/Particles can't replicate (listed below). Converting now will produce "
                        + "a visibly broken or incomplete effect. Either rebuild the effect manually with the "
                        + "DreamPark shader, or leave the material on its vendor shader.";
                    type = MessageType.Error;
                    break;
                case ConversionReadiness.WillLookDifferent:
                    msg = "Safe to convert if you accept that the result will look slightly different from "
                        + "the original. The source has properties (extra textures, PBR data, scrolling, "
                        + "etc.) that DreamPark/Particles doesn't have slots for — they get dropped, not "
                        + "translated. Skim the differences below and decide if the look is acceptable.";
                    type = MessageType.Warning;
                    break;
                case ConversionReadiness.ReadyWithApproximations:
                    msg = "Safe to convert. The vendor uses features that DreamPark/Particles replaces with "
                        + "simpler equivalents — pseudo-lighting (NdotL fake lighting) for lit shading, and "
                        + "Unity's standard TextureSheetAnimation module for motion-vector flipbooks. The "
                        + "converter sets these up automatically. The result WILL work as intended, just "
                        + "with simpler implementations than the vendor's. See the 'Approximated' section "
                        + "below for each property's specific approximation.";
                    type = MessageType.Info;
                    break;
                default:
                    msg = "Safe to convert. Every meaningful property on the source carries over via the "
                        + "converter's alias table. The result should render identically to the vendor "
                        + "version.";
                    type = MessageType.Info;
                    break;
            }
            EditorGUILayout.HelpBox(msg, type);
        }

        // Color-coded chip rendered next to the popup title. Mirrors the
        // row badge in the main window so the user can match the popup
        // to the row they clicked.
        private static void DrawReadinessBadge(ConversionReadiness r, string text)
        {
            Color tint;
            switch (r)
            {
                case ConversionReadiness.Blocked:                  tint = new Color(0.85f, 0.30f, 0.30f); break;
                case ConversionReadiness.WillLookDifferent:        tint = new Color(0.95f, 0.65f, 0.30f); break;
                case ConversionReadiness.ReadyWithApproximations:  tint = new Color(0.45f, 0.65f, 0.95f); break;
                default:                                           tint = new Color(0.45f, 0.78f, 0.45f); break;
            }
            var style = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = tint } };
            EditorGUILayout.LabelField(text, style, GUILayout.Width(260));
        }

        private void DrawFilters()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _showLow     = GUILayout.Toggle(_showLow,     $" Show negligible ({_report.LowCount})",   GUILayout.Width(170));
                _showIgnored = GUILayout.Toggle(_showIgnored, $" Show metadata ({_report.IgnoredCount})", GUILayout.Width(160));
                GUILayout.FlexibleSpace();
            }
        }

        // Renders one combined section spanning one OR multiple internal
        // severity levels. Items inside the section are ordered by their
        // underlying severity so the most impactful issues show first.
        private void DrawIssueSection(string title, DiffSeverity[] severities, Color tint)
        {
            var sevSet = new HashSet<DiffSeverity>(severities);
            var props = _report.unmapped.Where(e => sevSet.Contains(e.severity)).ToList();
            var kws   = _report.unmappedKeywords.Where(e => sevSet.Contains(e.severity)).ToList();
            if (props.Count == 0 && kws.Count == 0)
            {
                var prevCol = GUI.color;
                GUI.color = new Color(0.6f, 0.6f, 0.6f);
                EditorGUILayout.LabelField($"   {title}: none", EditorStyles.miniLabel);
                GUI.color = prevCol;
                return;
            }

            var prev = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prev;
            EditorGUILayout.LabelField($"{title}  ({props.Count + kws.Count})", EditorStyles.boldLabel);

            // Order by severity (Critical > High > Medium > Low > Ignored)
            // and then by property name for stable display.
            int Rank(DiffSeverity s) => -(int)s;  // Critical=4 → -4 → first
            foreach (var p in props.OrderBy(e => Rank(e.severity)).ThenBy(e => e.propertyName, StringComparer.Ordinal))
                DrawEntry(p, isKeyword: false);
            foreach (var k in kws.OrderBy(e => Rank(e.severity)).ThenBy(e => e.propertyName, StringComparer.Ordinal))
                DrawEntry(k, isKeyword: true);

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private static void DrawEntry(DiffEntry e, bool isKeyword)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var nameLabel = isKeyword ? $"⚑ {e.propertyName}" : e.propertyName;
                EditorGUILayout.LabelField(nameLabel, GUILayout.Width(220));
                EditorGUILayout.LabelField(isKeyword ? "keyword" : e.type.ToString(), GUILayout.Width(70));
                EditorGUILayout.LabelField(e.currentValue ?? "", GUILayout.Width(220));
                GUILayout.FlexibleSpace();
            }
            if (!string.IsNullOrEmpty(e.note))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(20);
                    EditorGUILayout.LabelField(e.note, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private void DrawMappedSection()
        {
            _showMapped = EditorGUILayout.Foldout(_showMapped,
                $"Mapped properties (carry over) — {_report.mapped.Count}", true);
            if (!_showMapped) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (var e in _report.mapped)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(e.propertyName, GUILayout.Width(220));
                        EditorGUILayout.LabelField(e.type.ToString(), GUILayout.Width(70));
                        EditorGUILayout.LabelField(e.currentValue ?? "", GUILayout.Width(220));
                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        private string BuildPlainTextReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Particle Conversion Diff");
            sb.AppendLine($"# Material:  {_materialPath}");
            sb.AppendLine($"# Source:    {_report.sourceShaderName}");
            sb.AppendLine($"# Target:    {_report.targetShaderName}");
            sb.AppendLine($"# Readiness: {_report.Headline}  —  {_report.SubHeadline}");
            sb.AppendLine();

            void Section(string label, IEnumerable<DiffEntry> entries)
            {
                var list = entries.ToList();
                if (list.Count == 0) return;
                sb.AppendLine($"## {label} ({list.Count})");
                foreach (var e in list)
                {
                    sb.AppendLine($"  - {e.propertyName}  [{e.type}]  = {e.currentValue}");
                    if (!string.IsNullOrEmpty(e.note)) sb.AppendLine($"      ↳ {e.note}");
                }
                sb.AppendLine();
            }

            // Same action-oriented sections as the popup body.
            Section("Won't carry over (visible breakage)",
                _report.unmapped.Where(e => e.severity == DiffSeverity.Critical)
                       .Concat(_report.unmappedKeywords.Where(e => e.severity == DiffSeverity.Critical)));
            Section("Will look different",
                _report.unmapped.Where(e => e.severity == DiffSeverity.High || e.severity == DiffSeverity.Medium)
                       .Concat(_report.unmappedKeywords.Where(e => e.severity == DiffSeverity.High || e.severity == DiffSeverity.Medium)));
            Section("Negligible",
                _report.unmapped.Where(e => e.severity == DiffSeverity.Low)
                       .Concat(_report.unmappedKeywords.Where(e => e.severity == DiffSeverity.Low)));
            Section("Mapped (carry over)", _report.mapped);

            return sb.ToString();
        }
    }
}
#endif
