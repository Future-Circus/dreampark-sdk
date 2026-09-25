#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using DreamPark;
using DreamPark.Editor;
using NUnit.Framework;
using UnityEngine;

public sealed class ArenaPackageStoreTests
{
    [Test]
    public void VersionOneLayoutMigratesWithoutLosingPriorityOrExclusions()
    {
        ArenaPackageStore.Data data = ArenaPackageStore.Deserialize(
            "{\"schemaVersion\":1,\"buckets\":[{\"widthFeet\":1,\"lengthFeet\":1,"
            + "\"guids\":[\"b\",\"a\"]}],\"excludedGuids\":[\"hidden\"]}");

        ArenaPackageStore.Reconcile(data, new[]
        {
            new ArenaPackageStore.Candidate("a", 1f, 1f),
            new ArenaPackageStore.Candidate("b", 1f, 1f),
            new ArenaPackageStore.Candidate("hidden", 1f, 1f),
        });

        Assert.That(data.schemaVersion, Is.EqualTo(ArenaPackageStore.CurrentVersion));
        Assert.That(data.buckets.Single().guids, Is.EqualTo(new[] { "b", "a" }));
        Assert.That(data.excludedGuids, Is.EqualTo(new[] { "hidden" }));
        Assert.That(data.sizeOverrides, Is.Empty);
    }

    [Test]
    public void NewerLayoutIsRejectedInsteadOfBeingReplacedWithEmptyData()
    {
        Assert.Throws<System.InvalidOperationException>(() => ArenaPackageStore.Deserialize(
            "{\"schemaVersion\":999,\"buckets\":[{\"widthFeet\":1,\"lengthFeet\":1,"
            + "\"guids\":[\"keep-me\"]}]}"));
    }

    [Test]
    public void ReconcileAutoIncludesEachAssetInRotationNormalizedWholeFootBucket()
    {
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, new[]
        {
            new ArenaPackageStore.Candidate("wide", 6.1f, 2.2f),
            new ArenaPackageStore.Candidate("small", 0.2f, 0.8f),
        });

        Assert.That(data.buckets.Select(bucket => (bucket.widthFeet, bucket.lengthFeet)),
            Is.EqualTo(new[] { (1, 1), (2, 6) }));
        Assert.That(data.buckets.SelectMany(bucket => bucket.guids),
            Is.EqualTo(new[] { "small", "wide" }));
    }

    [Test]
    public void ReconcilePreservesPriorityWhenDiscoveryOrderChanges()
    {
        var candidates = new[]
        {
            new ArenaPackageStore.Candidate("a", 2f, 3f),
            new ArenaPackageStore.Candidate("b", 3f, 2f),
        };
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, candidates);
        Assert.That(ArenaPackageStore.Move(data, 2, 3, "b", -1), Is.True);

        ArenaPackageStore.Reconcile(data, candidates.Reverse());

        Assert.That(data.buckets.Single().guids, Is.EqualTo(new[] { "b", "a" }));
    }

    [Test]
    public void MoveRelativeReordersBeforeAndAfterWithinSizeBucket()
    {
        var candidates = new[]
        {
            new ArenaPackageStore.Candidate("a", 2f, 3f),
            new ArenaPackageStore.Candidate("b", 2f, 3f),
            new ArenaPackageStore.Candidate("c", 2f, 3f),
            new ArenaPackageStore.Candidate("d", 2f, 3f),
        };
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, candidates);

        Assert.That(ArenaPackageStore.MoveRelative(data, 2, 3, "d", "b", false), Is.True);
        Assert.That(data.buckets.Single().guids, Is.EqualTo(new[] { "a", "d", "b", "c" }));
        Assert.That(ArenaPackageStore.MoveRelative(data, 2, 3, "a", "b", true), Is.True);
        Assert.That(data.buckets.Single().guids, Is.EqualTo(new[] { "d", "b", "a", "c" }));
    }

    [Test]
    public void MoveRelativeAcrossBucketsPersistsAsManualSizeOverride()
    {
        var candidates = new[]
        {
            new ArenaPackageStore.Candidate("pengo", 1f, 2f),
            new ArenaPackageStore.Candidate("coin", 1f, 1f),
        };
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, candidates);

        Assert.That(ArenaPackageStore.MoveRelative(data, 1, 2, 1, 1,
            "pengo", "coin", true), Is.True);
        ArenaPackageStore.Reconcile(data, candidates);

        Assert.That(data.buckets.Single().guids, Is.EqualTo(new[] { "coin", "pengo" }));
        Assert.That(data.sizeOverrides.Single().guid, Is.EqualTo("pengo"));
        Assert.That(data.sizeOverrides.Single().widthFeet, Is.EqualTo(1));
        Assert.That(data.sizeOverrides.Single().lengthFeet, Is.EqualTo(1));
    }

    [Test]
    public void MoveToCollapsedBucketHeaderSurvivesSaveLoadAndReconcile()
    {
        var candidates = new[]
        {
            new ArenaPackageStore.Candidate("pengo", 1f, 2f),
            new ArenaPackageStore.Candidate("coin", 1f, 1f),
            new ArenaPackageStore.Candidate("gem", 1f, 1f),
        };
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, candidates);

        Assert.That(ArenaPackageStore.MoveToBucketEnd(
            data, 1, 2, 1, 1, "pengo"), Is.True);
        string saved = JsonUtility.ToJson(data);
        data = ArenaPackageStore.Deserialize(saved);
        ArenaPackageStore.Reconcile(data, candidates.Reverse());

        Assert.That(data.buckets.Single().guids,
            Is.EqualTo(new[] { "coin", "gem", "pengo" }));
        Assert.That(data.sizeOverrides.Single().guid, Is.EqualTo("pengo"));
        Assert.That(data.sizeOverrides.Single().widthFeet, Is.EqualTo(1));
        Assert.That(data.sizeOverrides.Single().lengthFeet, Is.EqualTo(1));
    }

    [Test]
    public void ClearingManualSizeOverrideReturnsAssetToMeasuredBucket()
    {
        var candidates = new[]
        {
            new ArenaPackageStore.Candidate("pengo", 1f, 2f),
            new ArenaPackageStore.Candidate("coin", 1f, 1f),
        };
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, candidates);
        ArenaPackageStore.MoveRelative(data, 1, 2, 1, 1, "pengo", "coin", true);

        Assert.That(ArenaPackageStore.ClearSizeOverride(data, candidates, "pengo"), Is.True);

        Assert.That(data.sizeOverrides, Is.Empty);
        Assert.That(data.buckets.Single(bucket => bucket.widthFeet == 1
            && bucket.lengthFeet == 2).guids, Is.EqualTo(new[] { "pengo" }));
    }

    [Test]
    public void MissingOneFootCoverageDoesNotCreateAnEmptyBucket()
    {
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, new[]
        {
            new ArenaPackageStore.Candidate("only-large", 4f, 6f),
        });

        Assert.That(data.buckets.Count, Is.EqualTo(1));
        Assert.That(data.buckets[0].widthFeet, Is.EqualTo(4));
        Assert.That(data.buckets[0].lengthFeet, Is.EqualTo(6));
        Assert.That(data.buckets[0].guids, Is.EqualTo(new[] { "only-large" }));
    }

    [Test]
    public void ExclusionPersistsAndReincludeReturnsAssetToNaturalBucket()
    {
        var candidates = new List<ArenaPackageStore.Candidate>
        {
            new ArenaPackageStore.Candidate("prop", 1f, 2f),
        };
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, candidates);

        Assert.That(ArenaPackageStore.SetExcluded(data, candidates, "prop", true), Is.True);
        Assert.That(data.buckets, Is.Empty);
        Assert.That(data.excludedGuids, Is.EqualTo(new[] { "prop" }));

        Assert.That(ArenaPackageStore.SetExcluded(data, candidates, "prop", false), Is.True);
        Assert.That(data.excludedGuids, Is.Empty);
        Assert.That(data.buckets.Single().guids, Is.EqualTo(new[] { "prop" }));
    }

    [Test]
    public void FootprintChangeMovesAssetWithoutDuplicatingIt()
    {
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, new[]
        {
            new ArenaPackageStore.Candidate("asset", 1f, 1f),
        });

        ArenaPackageStore.Reconcile(data, new[]
        {
            new ArenaPackageStore.Candidate("asset", 4.2f, 2.1f),
        });

        Assert.That(data.buckets.Count, Is.EqualTo(1));
        Assert.That(data.buckets[0].widthFeet, Is.EqualTo(2));
        Assert.That(data.buckets[0].lengthFeet, Is.EqualTo(4));
        Assert.That(data.buckets[0].guids, Is.EqualTo(new[] { "asset" }));
    }

    [Test]
    public void PlainLevelTemplateIsNotAnArenaCandidate()
    {
        GameObject prefab = new GameObject("Plain Level");
        try
        {
            prefab.AddComponent<LevelTemplate>();

            ArenaPackageStore.Candidate candidate =
                ArenaPackageStore.CandidateForPrefab("level", prefab);

            Assert.That(candidate.IsValid, Is.False);
            Assert.That(ArenaPackageStore.Reconcile(null, new[] { candidate }).buckets,
                Is.Empty);
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void AttractionWithoutPositiveSizeIsNotAnArenaCandidate()
    {
        GameObject prefab = new GameObject("Unsized Attraction");
        try
        {
            AttractionTemplate attraction = prefab.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = Vector2.zero;

            ArenaPackageStore.Candidate candidate =
                ArenaPackageStore.CandidateForPrefab("attraction", prefab);

            Assert.That(candidate.IsValid, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void AttractionArenaBucketUsesSmallestValidShrinkFootprint()
    {
        GameObject prefab = new GameObject("Shrinkable Attraction");
        try
        {
            AttractionTemplate attraction = prefab.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = new Vector2(6f, 9f);
            Vector2 authored = new Vector2(6f, 9f) * 0.3048f;
            Vector2 shrink = new Vector2(3.9f, 5.9f) * 0.3048f;
            Vector2 grow = Vector2.Scale(authored, attraction.maxGrowthScale);
            attraction.SetPackingBake(new AttractionPackingBake(
                authored, authored, shrink, grow, shrink,
                new List<AttractionPropPackingPose>()));

            ArenaPackageStore.Candidate candidate =
                ArenaPackageStore.CandidateForPrefab("attraction", prefab);

            Assert.That(candidate.widthFeet, Is.EqualTo(3));
            Assert.That(candidate.lengthFeet, Is.EqualTo(5));
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void PropWithoutMeasurableOrManualSizeIsNotAnArenaCandidate()
    {
        GameObject prefab = new GameObject("Unsized Prop");
        try
        {
            PropTemplate prop = prefab.AddComponent<PropTemplate>();
            prop.useColliderBounds = true;

            ArenaPackageStore.Candidate candidate =
                ArenaPackageStore.CandidateForPrefab("prop", prefab);

            Assert.That(candidate.IsValid, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void PropWithPartialColliderCoverageIsNotAnArenaCandidate()
    {
        GameObject prefab = new GameObject("Procedural Pit");
        try
        {
            PropTemplate prop = prefab.AddComponent<PropTemplate>();
            prop.useColliderBounds = true;
            prefab.AddComponent<BoxCollider>().size = Vector3.one * 0.49f;
            prefab.AddComponent<MeshCollider>().sharedMesh = null;

            ArenaPackageStore.Candidate candidate =
                ArenaPackageStore.CandidateForPrefab("pit", prefab);

            Assert.That(candidate.IsValid, Is.False);
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void ManualPropFootprintUsesAuthoredDimensions()
    {
        GameObject prefab = new GameObject("Manual Pit");
        try
        {
            PropTemplate prop = prefab.AddComponent<PropTemplate>();
            prop.useColliderBounds = false;
            prop.customFootprintMeters = new Vector2(2.7610629f, 4.2608229f);

            ArenaPackageStore.Candidate candidate =
                ArenaPackageStore.CandidateForPrefab("pit", prefab);

            Assert.That(candidate.IsValid, Is.True);
            Assert.That(candidate.widthFeet, Is.EqualTo(9));
            Assert.That(candidate.lengthFeet, Is.EqualTo(13));
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void ColliderSizedPropRoundsDownToWholeFootArenaBucket()
    {
        GameObject prefab = new GameObject("Coin");
        try
        {
            PropTemplate prop = prefab.AddComponent<PropTemplate>();
            prop.useColliderBounds = true;
            SphereCollider collider = prefab.AddComponent<SphereCollider>();
            collider.radius = 0.2f;

            ArenaPackageStore.Candidate candidate =
                ArenaPackageStore.CandidateForPrefab("coin", prefab);

            Assert.That(candidate.IsValid, Is.True);
            Assert.That(candidate.widthFeet, Is.EqualTo(1));
            Assert.That(candidate.lengthFeet, Is.EqualTo(1));
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }
}
#endif
