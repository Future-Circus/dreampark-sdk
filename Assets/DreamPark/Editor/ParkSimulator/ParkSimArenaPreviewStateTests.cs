#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using DreamPark.Editor;
using NUnit.Framework;
using UnityEngine;

namespace DreamPark.ParkSim
{
    public sealed class ParkSimArenaPreviewStateTests
    {
        [Test]
        public void Configure_StartsAtSmallestBucketAndFirstCandidate()
        {
            var state = NewState();

            Assert.That(state.BucketIndex, Is.EqualTo(0));
            Assert.That(state.FloorFeet, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(state.CurrentGuid, Is.EqualTo("one-a"));
            Assert.That(state.Automatic, Is.True);
        }

        [Test]
        public void Automatic_PingPongsAndAdvancesCandidateOnReturn()
        {
            var state = NewState();
            float leg = ParkSimArenaPreviewState.HoldSeconds
                + ParkSimArenaPreviewState.TransitionSeconds;

            Assert.That(state.Tick(leg), Is.True);
            Assert.That(state.CurrentGuid, Is.EqualTo("two"));
            Assert.That(state.Tick(leg), Is.True);
            Assert.That(state.CurrentGuid, Is.EqualTo("three"));
            Assert.That(state.Tick(leg), Is.True);
            Assert.That(state.CurrentGuid, Is.EqualTo("two"));
            Assert.That(state.Tick(leg), Is.True);
            Assert.That(state.CurrentGuid, Is.EqualTo("one-b"));
        }

        [Test]
        public void ManualStep_PausesSnapsAndUsesPerBucketCursor()
        {
            var state = NewState();

            Assert.That(state.StepSize(1), Is.True);
            Assert.That(state.Automatic, Is.False);
            Assert.That(state.FloorFeet, Is.EqualTo(new Vector2(1f, 2f)));
            Assert.That(state.CurrentGuid, Is.EqualTo("two"));
            Assert.That(state.StepSize(-1), Is.True);
            Assert.That(state.CurrentGuid, Is.EqualTo("one-b"));
        }

        [Test]
        public void BestFit_AllowsRotationAndSelectsLargestCompatibleBucket()
        {
            var state = NewState();

            Assert.That(state.BestFitIndex(new Vector2(2f, 1f)), Is.EqualTo(1));
            Assert.That(state.BestFitIndex(new Vector2(2f, 2f)), Is.EqualTo(2));
            Assert.That(state.BestFitIndex(new Vector2(0.5f, 0.5f)), Is.EqualTo(-1));
        }

        [Test]
        public void SingleBucket_AutomaticStillCyclesCandidates()
        {
            var state = new ParkSimArenaPreviewState();
            state.Configure("game", new[] { Bucket(1, 1, "a", "b") });

            Assert.That(state.Tick(ParkSimArenaPreviewState.HoldSeconds + 0.01f), Is.True);
            Assert.That(state.CurrentGuid, Is.EqualTo("b"));
        }

        [Test]
        public void NothingToCycle_DoesNotRunOrRestartAutomaticPlayback()
        {
            var state = new ParkSimArenaPreviewState();
            state.Configure("game", new[] { Bucket(1, 1, "only") });

            Assert.That(state.Automatic, Is.False);
            Assert.That(state.CanCycle, Is.False);
            state.ToggleAutomatic();
            Assert.That(state.Automatic, Is.False);
            Assert.That(state.Tick(100f), Is.False);

            state.Configure("empty", new ArenaPackageStore.Bucket[0]);
            Assert.That(state.Automatic, Is.False);
            Assert.That(state.CanCycle, Is.False);
        }

        [Test]
        public void IncomparableShapes_SnapWithoutDisplayingContentOnAnUnsafeTween()
        {
            var state = new ParkSimArenaPreviewState();
            state.Configure("game", new[]
            {
                Bucket(2, 6, "wide"),
                Bucket(3, 4, "square"),
            });

            Assert.That(state.Tick(ParkSimArenaPreviewState.HoldSeconds + 0.01f), Is.True);
            Assert.That(state.Transitioning, Is.False);
            Assert.That(state.FloorFeet, Is.EqualTo(new Vector2(3f, 4f)));
            Assert.That(state.CurrentGuid, Is.EqualTo("square"));
        }

        [Test]
        public void ArenaOrientation_UsesTheDirectionWithLeastOverflow()
        {
            Assert.That(ParkSimPackageTest.ShouldRotateArenaFootprint(
                new Vector2(8f, 4f), new Vector2(4f, 8f)), Is.True);
            Assert.That(ParkSimPackageTest.ShouldRotateArenaFootprint(
                new Vector2(4f, 8f), new Vector2(4f, 8f)), Is.False);
        }

        [Test]
        public void ArenaGround_IsInvisibleButRetainsColliderAndOutline()
        {
            GameObject ground = ParkSimPark.SpawnArenaGround(
                new Vector2(2f, 3f), new List<string>());
            try
            {
                Assert.That(ground.GetComponent<MeshRenderer>().enabled, Is.False);
                Assert.That(ground.GetComponent<Collider>().enabled, Is.True);
                Assert.That(ground.GetComponent<ParkSimArenaGroundOutline>(), Is.Not.Null);
                Assert.That(ground.transform.localScale.x, Is.EqualTo(0.6096f).Within(0.0001f));
                Assert.That(ground.transform.localScale.z, Is.EqualTo(0.9144f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(ground);
            }
        }

        private static ParkSimArenaPreviewState NewState()
        {
            var state = new ParkSimArenaPreviewState();
            state.Configure("game", new[]
            {
                Bucket(2, 2, "three"),
                Bucket(1, 2, "two"),
                Bucket(1, 1, "one-a", "one-b"),
            });
            return state;
        }

        private static ArenaPackageStore.Bucket Bucket(int width, int length,
            params string[] guids)
            => new ArenaPackageStore.Bucket
            {
                widthFeet = width,
                lengthFeet = length,
                guids = new List<string>(guids),
            };
    }
}
#endif
