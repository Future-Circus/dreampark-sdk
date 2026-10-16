#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.Editor;
using UnityEditor;
using UnityEngine;

namespace DreamPark {
    /// <summary>
    /// Sequence dev tip: names the level whose smallest layout frame sets the
    /// Sequence's minimum room — the one worth a smaller flexible frame.
    /// The package minimum is the per-axis max of every level's minimum
    /// (DreamSequencePackageCompiler.LevelMinimumFootprint), so width and length
    /// can be held by different levels, and a tie means one new frame alone
    /// will not gain any room on that axis.
    /// </summary>
    public partial class ContentUploaderPanel
    {
        private sealed class SequenceMinimumLevel
        {
            public string name;
            public string assetPath;
            public bool hasLayoutFrames;
            public Vector2 minimumFeet; // Sequence axes (x = width, y = length)
        }

        private sealed class SequenceMinimumTipData
        {
            public Vector2 roomFeet;
            public List<SequenceMinimumLevel> widthLimiters;
            public List<SequenceMinimumLevel> lengthLimiters;
        }

        // Levels within this of the largest minimum count as tied (~0.6 in).
        private const float SequenceMinimumTieFeet = 0.05f;

        // Frame ladders are not free to rebuild, so the tip is recomputed only
        // when the Sequence's levels change or the content roots refresh (any
        // project change, which includes saving a layout frame or a rebake).
        private SequenceMinimumTipData sequenceMinimumTip;
        private string sequenceMinimumTipKey;
        private int contentRootsVersion;

        private void DrawSequenceMinimumTip()
        {
            List<string> stageGuids = DreamSequencePackageCompiler.StageGuids(sequenceLayout)
                .Distinct(StringComparer.Ordinal).ToList();
            string key = contentId + "|" + contentRootsVersion + "|" + string.Join(",", stageGuids);
            if (!string.Equals(key, sequenceMinimumTipKey, StringComparison.Ordinal))
            {
                sequenceMinimumTipKey = key;
                sequenceMinimumTip = ComputeSequenceMinimumTip(stageGuids);
            }
            SequenceMinimumTipData tip = sequenceMinimumTip;
            if (tip == null) return;

            EditorGUILayout.HelpBox(SequenceMinimumTipText(tip), MessageType.Info);
            Rect box = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(box, MouseCursor.Link);
            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && box.Contains(evt.mousePosition))
            {
                UnityEngine.Object[] prefabs = tip.widthLimiters.Concat(tip.lengthLimiters)
                    .Select(level => level.assetPath).Distinct(StringComparer.Ordinal)
                    .Select(path => (UnityEngine.Object)AssetDatabase.LoadAssetAtPath<GameObject>(path))
                    .Where(prefab => prefab != null).ToArray();
                if (prefabs.Length > 0)
                {
                    Selection.objects = prefabs;
                    EditorGUIUtility.PingObject(prefabs[0]);
                }
                evt.Use();
            }
            GUILayout.Space(2f);
        }

        // Mirrors DreamSequencePackageCompiler.Compile: Start / Overlay / Game Over
        // never rotate; stage attractions rotate when that fits better, and
        // Sequence-incompatible ones are skipped. Levels the compile would have
        // to bake first, or that cannot fit the room at all, are left out here —
        // the compile/pre-upload checks report those.
        private SequenceMinimumTipData ComputeSequenceMinimumTip(List<string> stageGuids)
        {
            if (string.IsNullOrEmpty(contentId)) return null;
            var levels = new List<SequenceMinimumLevel>();

            void AddLevel(string path, bool isStage)
            {
                if (string.IsNullOrEmpty(path)) return;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AttractionTemplate attraction = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                if (attraction == null) return;
                if (isStage && (attraction is DreamSequenceTemplate
                    || !DreamSequenceCompatibility.IsCompatible(attraction))) return;
                try
                {
                    Vector2 meters = DreamSequencePackageCompiler.LevelMinimumFootprint(attraction, isStage);
                    levels.Add(new SequenceMinimumLevel
                    {
                        name = prefab.name,
                        assetPath = path,
                        hasLayoutFrames = attraction.HasLayoutFrames,
                        minimumFeet = meters * FeetPerMeter,
                    });
                }
                catch (InvalidOperationException) { }
            }

            AddLevel(DreamSequenceGenerator.StartLevelPath(contentId), false);
            // Oct 11 2026: the Overlay and Game Over no longer set the minimum
            // room (DreamSequencePackageCompiler.Compile), so they are never
            // named as the level that limits shrinking.
            foreach (string guid in stageGuids)
                AddLevel(AssetDatabase.GUIDToAssetPath(guid), true);
            if (levels.Count == 0) return null;

            float width = levels.Max(level => level.minimumFeet.x);
            float length = levels.Max(level => level.minimumFeet.y);
            return new SequenceMinimumTipData
            {
                roomFeet = new Vector2(width, length),
                widthLimiters = levels.Where(level => level.minimumFeet.x >= width - SequenceMinimumTieFeet).ToList(),
                lengthLimiters = levels.Where(level => level.minimumFeet.y >= length - SequenceMinimumTieFeet).ToList(),
            };
        }

        private static string SequenceMinimumTipText(SequenceMinimumTipData tip)
        {
            string room = $"{tip.roomFeet.x:0.#} × {tip.roomFeet.y:0.#} ft";
            bool sameLimiters = tip.widthLimiters.Select(level => level.assetPath)
                .SequenceEqual(tip.lengthLimiters.Select(level => level.assetPath));
            List<SequenceMinimumLevel> limiters = tip.widthLimiters.Concat(tip.lengthLimiters)
                .GroupBy(level => level.assetPath, StringComparer.Ordinal)
                .Select(group => group.First()).ToList();

            string text;
            if (sameLimiters)
            {
                bool tied = tip.widthLimiters.Count > 1;
                text = $"Tip: {NameList(tip.widthLimiters)} {(tied ? "have" : "has")} the largest minimum size in this Sequence"
                    + (tied ? " (tied)" : "")
                    + $", so the Sequence can't shrink below {room}.";
            }
            else
            {
                text = $"Tip: This Sequence can't shrink below {room}. "
                    + $"Width is held by {NameList(tip.widthLimiters)} ({tip.roomFeet.x:0.#} ft)"
                    + (tip.widthLimiters.Count > 1 ? ", tied" : "")
                    + $"; length by {NameList(tip.lengthLimiters)} ({tip.roomFeet.y:0.#} ft)"
                    + (tip.lengthLimiters.Count > 1 ? ", tied" : "") + ".";
            }

            if (limiters.Count == 1)
            {
                SequenceMinimumLevel level = limiters[0];
                string subject = sameLimiters ? "it" : level.name;
                text += level.hasLayoutFrames
                    ? $" A smaller layout frame on {subject} would let the whole Sequence fit tighter rooms."
                    : $" Adding a Minimum layout frame to {subject} would let the whole Sequence fit tighter rooms.";
            }
            else
            {
                text += " Smaller layout frames on these would let the whole Sequence fit tighter rooms"
                    + (tip.widthLimiters.Count > 1 || tip.lengthLimiters.Count > 1
                        ? " (tied levels all need one before that axis can shrink)." : ".");
            }
            return text + " Click to select.";
        }

        private static string NameList(List<SequenceMinimumLevel> levels)
        {
            if (levels.Count == 1) return levels[0].name;
            if (levels.Count == 2) return levels[0].name + " and " + levels[1].name;
            return $"{levels[0].name}, {levels[1].name} and {levels.Count - 2} more";
        }
    }
}
#endif
