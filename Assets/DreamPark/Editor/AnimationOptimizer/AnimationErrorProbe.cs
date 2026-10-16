#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace DreamPark.EditorTools.AnimationOptimization
{
    /// <summary>
    /// Oct 2026: verifies a compression change the way the runtime plays it.
    ///
    /// On CrashCourse, switching FBX clips to Optimal matched the old import
    /// exactly ON the 30 fps keys but cut corners BETWEEN them (Optimal can
    /// switch a clip to a dense linear format): up to 10.4° at the 60 Hz
    /// in-between frames, which Quest draws at 72/90 Hz. Editor curves don't
    /// show this, so we sample through the runtime path
    /// (PlayableGraph + AnimationClipPlayable) at 60 Hz, before and after the
    /// importer change, and the executor reverts an FBX whose clips move more
    /// than <see cref="MaxRotationErrorDegrees"/> / <see cref="MaxPositionErrorMm"/>.
    /// </summary>
    public static class AnimationErrorProbe
    {
        public const float SampleRateHz = 60f;
        public const float MaxRotationErrorDegrees = 0.5f;
        public const float MaxPositionErrorMm = 5f;

        /// Local poses of every transform of a model, per frame, for one clip.
        public sealed class PoseSeries
        {
            public string[] paths;
            public List<Quaternion[]> rotations = new List<Quaternion[]>();
            public List<Vector3[]> positions = new List<Vector3[]>();
        }

        public struct ErrorReport
        {
            public float maxRotationDegrees;
            public float maxPositionMm;
            public string worstClip;
            public bool WithinLimits => maxRotationDegrees <= MaxRotationErrorDegrees && maxPositionMm <= MaxPositionErrorMm;
            public override string ToString() => $"{maxRotationDegrees:F2}° / {maxPositionMm:F1} mm (worst clip {worstClip ?? "-"})";
        }

        /// Pure: the largest per-frame difference between two samplings of the
        /// same clips (matched by clip name and transform path).
        public static ErrorReport Compare(IDictionary<string, PoseSeries> before, IDictionary<string, PoseSeries> after)
        {
            var report = new ErrorReport();
            if (before == null || after == null) return report;
            foreach (var pair in before)
            {
                if (!after.TryGetValue(pair.Key, out PoseSeries b) || b == null) continue;
                PoseSeries a = pair.Value;
                var index = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < b.paths.Length; i++) index[b.paths[i]] = i;
                int frames = Math.Min(a.rotations.Count, b.rotations.Count);
                for (int f = 0; f < frames; f++)
                    for (int t = 0; t < a.paths.Length; t++)
                    {
                        if (!index.TryGetValue(a.paths[t], out int bt)) continue;
                        float rot = Quaternion.Angle(a.rotations[f][t], b.rotations[f][bt]);
                        float pos = (a.positions[f][t] - b.positions[f][bt]).magnitude * 1000f;
                        if (rot > report.maxRotationDegrees || pos > report.maxPositionMm) report.worstClip = pair.Key;
                        report.maxRotationDegrees = Math.Max(report.maxRotationDegrees, rot);
                        report.maxPositionMm = Math.Max(report.maxPositionMm, pos);
                    }
            }
            return report;
        }

        /// Samples every clip of a model file at 60 Hz through the runtime
        /// playables path (legacy clips through SampleAnimation). Returns null
        /// when the file has no model to drive.
        public static Dictionary<string, PoseSeries> Sample(string modelPath)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (model == null) return null;
            var clips = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>()
                .Where(c => c != null && !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToList();
            if (clips.Count == 0) return null;

            var result = new Dictionary<string, PoseSeries>(StringComparer.Ordinal);
            GameObject go = Object.Instantiate(model);
            go.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                Transform[] transforms = go.GetComponentsInChildren<Transform>(true);
                string[] paths = transforms.Select(t => AnimationUtility.CalculateTransformPath(t, go.transform)).ToArray();
                Animator animator = go.GetComponent<Animator>();
                foreach (AnimationClip clip in clips)
                {
                    var series = new PoseSeries { paths = paths };
                    int frames = Mathf.Max(1, Mathf.CeilToInt(clip.length * SampleRateHz)) + 1;
                    if (clip.legacy || animator == null)
                    {
                        for (int f = 0; f < frames; f++)
                        {
                            clip.SampleAnimation(go, Mathf.Min(clip.length, f / SampleRateHz));
                            Record(series, transforms);
                        }
                    }
                    else
                    {
                        PlayableGraph graph = PlayableGraph.Create("DreamPark animation error probe");
                        try
                        {
                            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                            var output = AnimationPlayableOutput.Create(graph, "probe", animator);
                            var playable = AnimationClipPlayable.Create(graph, clip);
                            playable.SetApplyFootIK(false);
                            output.SetSourcePlayable(playable);
                            for (int f = 0; f < frames; f++)
                            {
                                playable.SetTime(Mathf.Min(clip.length, f / SampleRateHz));
                                graph.Evaluate(0f);
                                Record(series, transforms);
                            }
                        }
                        finally
                        {
                            graph.Destroy();
                        }
                    }
                    result[clip.name] = series;
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            return result;
        }

        private static void Record(PoseSeries series, Transform[] transforms)
        {
            var rot = new Quaternion[transforms.Length];
            var pos = new Vector3[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                rot[i] = transforms[i].localRotation;
                pos[i] = transforms[i].localPosition;
            }
            series.rotations.Add(rot);
            series.positions.Add(pos);
        }
    }
}
#endif
