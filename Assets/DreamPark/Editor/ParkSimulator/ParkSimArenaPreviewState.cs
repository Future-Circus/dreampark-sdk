#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using DreamPark.Editor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    /// Pure navigation state for Arena Test. Scene/UI code only consumes this
    /// state; authored Arena metadata is never changed by previewing it.
    internal sealed class ParkSimArenaPreviewState
    {
        internal const float TransitionSeconds = 2.5f;
        internal const float HoldSeconds = 1.5f;

        private readonly List<ArenaPackageStore.Bucket> buckets =
            new List<ArenaPackageStore.Bucket>();
        private readonly Dictionary<string, int> candidateCursors =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private string signature;
        private int selectedIndex;
        private int groundIndex;
        private int direction = 1;
        private int transitionTargetIndex;
        private float transitionProgress;
        private float holdRemaining;
        private Vector2 transitionStart;
        private Vector2 transitionTarget;

        public bool Automatic { get; private set; } = true;
        public bool Transitioning { get; private set; }
        public Vector2 FloorFeet { get; private set; } = Vector2.one;
        public int BucketIndex => selectedIndex;
        public int BucketCount => buckets.Count;
        public int CandidateIndex => CurrentBucket == null ? 0
            : CandidateCursor(CurrentBucket);
        public int CandidateCount => CurrentBucket?.guids?.Count ?? 0;
        public string CurrentGuid => CandidateCount == 0 ? null
            : CurrentBucket.guids[Mathf.Clamp(CandidateIndex, 0, CandidateCount - 1)];
        public bool CanStepSmaller => buckets.Count > 0 && selectedIndex > 0;
        public bool CanStepLarger => buckets.Count > 0 && selectedIndex < buckets.Count - 1;
        public bool CanCycle => buckets.Count > 1 || CandidateCount > 1;
        private ArenaPackageStore.Bucket CurrentBucket => buckets.Count == 0
            ? null : buckets[Mathf.Clamp(selectedIndex, 0, buckets.Count - 1)];

        public void Configure(string contentId, IEnumerable<ArenaPackageStore.Bucket> source)
        {
            var next = (source ?? Enumerable.Empty<ArenaPackageStore.Bucket>())
                .Where(bucket => bucket != null && bucket.widthFeet > 0
                    && bucket.lengthFeet > 0 && bucket.guids != null
                    && bucket.guids.Count > 0)
                .OrderBy(bucket => (long)bucket.widthFeet * bucket.lengthFeet)
                .ThenBy(bucket => Mathf.Min(bucket.widthFeet, bucket.lengthFeet))
                .ThenBy(bucket => Mathf.Max(bucket.widthFeet, bucket.lengthFeet))
                .ToList();
            string nextSignature = (contentId ?? "") + "|" + string.Join("|", next.Select(bucket =>
                bucket.widthFeet + "x" + bucket.lengthFeet + ":" + string.Join(",", bucket.guids)));
            if (string.Equals(signature, nextSignature, StringComparison.Ordinal)) return;

            signature = nextSignature;
            buckets.Clear();
            buckets.AddRange(next);
            candidateCursors.Clear();
            selectedIndex = 0;
            groundIndex = 0;
            direction = 1;
            Transitioning = false;
            transitionProgress = 0f;
            holdRemaining = HoldSeconds;
            Automatic = CanCycle;
            FloorFeet = buckets.Count == 0 ? Vector2.one : SizeOf(buckets[0]);
            if (buckets.Count > 0) candidateCursors[Key(buckets[0])] = 0;
        }

        public void Reset()
        {
            signature = null;
            buckets.Clear();
            candidateCursors.Clear();
            selectedIndex = 0;
            groundIndex = 0;
            direction = 1;
            transitionTargetIndex = 0;
            transitionProgress = 0f;
            holdRemaining = HoldSeconds;
            transitionStart = Vector2.one;
            transitionTarget = Vector2.one;
            Automatic = false;
            Transitioning = false;
            FloorFeet = Vector2.one;
        }

        public bool StepSize(int delta)
        {
            Automatic = false;
            Transitioning = false;
            transitionProgress = 0f;
            if (buckets.Count == 0 || delta == 0) return false;
            int next = Mathf.Clamp(selectedIndex + Math.Sign(delta), 0, buckets.Count - 1);
            if (next == selectedIndex) return false;
            groundIndex = next;
            FloorFeet = SizeOf(buckets[next]);
            holdRemaining = HoldSeconds;
            return EnterBucket(next);
        }

        public void ToggleAutomatic()
        {
            if (!Automatic && !CanCycle) return;
            Automatic = !Automatic;
            if (Automatic) holdRemaining = HoldSeconds;
        }

        /// Returns true when the selected bucket/candidate changed and the
        /// simulator must rebuild the preview content.
        public bool Tick(float deltaSeconds)
        {
            if (!Automatic || buckets.Count == 0 || deltaSeconds <= 0f) return false;
            bool changed = false;

            if (!Transitioning)
            {
                holdRemaining -= deltaSeconds;
                if (holdRemaining > 0f) return false;
                if (buckets.Count == 1)
                {
                    if ((buckets[0].guids?.Count ?? 0) <= 1)
                    {
                        Automatic = false;
                        return false;
                    }
                    AdvanceCandidate(buckets[0]);
                    holdRemaining = HoldSeconds;
                    return true;
                }

                int next = groundIndex + direction;
                if (next < 0 || next >= buckets.Count)
                {
                    direction *= -1;
                    next = groundIndex + direction;
                }
                transitionTargetIndex = Mathf.Clamp(next, 0, buckets.Count - 1);
                transitionStart = FloorFeet;
                transitionTarget = SizeOf(buckets[transitionTargetIndex]);
                transitionProgress = 0f;
                Vector2 currentSize = SizeOf(buckets[groundIndex]);
                bool targetContainsCurrent = Fits(currentSize, transitionTarget);
                bool currentContainsTarget = Fits(transitionTarget, currentSize);
                if (!targetContainsCurrent && !currentContainsTarget)
                {
                    groundIndex = transitionTargetIndex;
                    FloorFeet = transitionTarget;
                    Transitioning = false;
                    holdRemaining = HoldSeconds;
                    return EnterBucket(transitionTargetIndex);
                }
                Transitioning = true;

            }

            transitionProgress = Mathf.Clamp01(transitionProgress
                + deltaSeconds / TransitionSeconds);
            FloorFeet = Vector2.Lerp(transitionStart, transitionTarget, transitionProgress);

            int bestFit = BestFitIndex(FloorFeet);
            if (bestFit >= 0 && bestFit != selectedIndex)
                changed |= EnterBucket(bestFit);

            if (transitionProgress >= 1f)
            {
                groundIndex = transitionTargetIndex;
                Transitioning = false;
                holdRemaining = HoldSeconds;
            }
            return changed;
        }

        internal int BestFitIndex(Vector2 rectangleFeet)
        {
            int best = -1;
            for (int i = 0; i < buckets.Count; i++)
            {
                ArenaPackageStore.Bucket bucket = buckets[i];
                bool fits = bucket.widthFeet <= rectangleFeet.x
                    && bucket.lengthFeet <= rectangleFeet.y;
                bool fitsRotated = bucket.lengthFeet <= rectangleFeet.x
                    && bucket.widthFeet <= rectangleFeet.y;
                if (fits || fitsRotated) best = i;
            }
            return best;
        }

        private static bool Fits(Vector2 contentFeet, Vector2 rectangleFeet)
            => (contentFeet.x <= rectangleFeet.x && contentFeet.y <= rectangleFeet.y)
                || (contentFeet.y <= rectangleFeet.x && contentFeet.x <= rectangleFeet.y);

        private bool EnterBucket(int index)
        {
            if (index < 0 || index >= buckets.Count || index == selectedIndex) return false;
            selectedIndex = index;
            AdvanceCandidate(buckets[index]);
            return true;
        }

        private void AdvanceCandidate(ArenaPackageStore.Bucket bucket)
        {
            string key = Key(bucket);
            int count = bucket.guids?.Count ?? 0;
            if (count == 0) return;
            if (!candidateCursors.TryGetValue(key, out int current))
                candidateCursors[key] = 0;
            else candidateCursors[key] = (current + 1) % count;
        }

        private int CandidateCursor(ArenaPackageStore.Bucket bucket)
            => candidateCursors.TryGetValue(Key(bucket), out int value) ? value : 0;

        private static string Key(ArenaPackageStore.Bucket bucket)
            => bucket.widthFeet + "x" + bucket.lengthFeet;

        private static Vector2 SizeOf(ArenaPackageStore.Bucket bucket)
            => new Vector2(bucket.widthFeet, bucket.lengthFeet);
    }
}
#endif
