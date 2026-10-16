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

    // ── Layout frames (store schema 3: candidate keys) ────────────────────

    private const float Ft = 0.3048f;

    [Test]
    public void VersionTwoLayoutLoadsGuidsAsBaseCandidateKeys()
    {
        ArenaPackageStore.Data data = ArenaPackageStore.Deserialize(
            "{\"schemaVersion\":2,\"buckets\":[{\"widthFeet\":8,\"lengthFeet\":10,"
            + "\"guids\":[\"other\",\"g\"]}],\"excludedGuids\":[\"hidden\"],"
            + "\"sizeOverrides\":[]}");

        ArenaPackageStore.Reconcile(data, new[]
        {
            new ArenaPackageStore.Candidate("g", AttractionTemplate.BaseFrameId, 8.5f, 10.2f, 400f * Ft * Ft),
            new ArenaPackageStore.Candidate("g", "fwide0001", 12.9f, 25.3f, 600f * Ft * Ft),
            new ArenaPackageStore.Candidate("other", 8f, 10f),
            new ArenaPackageStore.Candidate("hidden", 1f, 1f),
        });

        Assert.That(data.schemaVersion, Is.EqualTo(3));
        Assert.That(ArenaPackageStore.CurrentVersion, Is.EqualTo(3));
        Assert.That(data.buckets.Select(bucket => bucket.guids.ToArray()), Is.EqualTo(new[]
        {
            new[] { "other", "g" },
            new[] { "g:fwide0001" },
        }), "Schema 2 GUIDs keep their priority as Base keys; the new frame is appended.");
        Assert.That(data.excludedGuids, Is.EqualTo(new[] { "hidden" }));
    }

    [Test]
    public void SchemaNewerThanThreeIsStillRejected()
    {
        Assert.Throws<System.InvalidOperationException>(() => ArenaPackageStore.Deserialize(
            "{\"schemaVersion\":4,\"buckets\":[{\"widthFeet\":1,\"lengthFeet\":1,"
            + "\"guids\":[\"keep-me\"]}]}"));
        Assert.DoesNotThrow(() => ArenaPackageStore.Deserialize(
            "{\"schemaVersion\":3,\"buckets\":[{\"widthFeet\":1,\"lengthFeet\":1,"
            + "\"guids\":[\"g:fbig00001\"]}]}"));
    }

    [Test]
    public void CandidateKeysRoundTripBaseAndFrameIds()
    {
        Assert.That(ArenaPackageStore.CandidateKey("g", "base"), Is.EqualTo("g"));
        Assert.That(ArenaPackageStore.CandidateKey("g", null), Is.EqualTo("g"));
        Assert.That(ArenaPackageStore.CandidateKey("g", "minimum"), Is.EqualTo("g:minimum"));

        Assert.That(ArenaPackageStore.TryParseCandidateKey("g", out string guid, out string frame), Is.True);
        Assert.That((guid, frame), Is.EqualTo(("g", "base")));
        Assert.That(ArenaPackageStore.TryParseCandidateKey("g:fbig00001", out guid, out frame), Is.True);
        Assert.That((guid, frame), Is.EqualTo(("g", "fbig00001")));
        Assert.That(ArenaPackageStore.TryParseCandidateKey("g:Not Valid", out _, out _), Is.False);
        Assert.That(ArenaPackageStore.TryParseCandidateKey(":fbig00001", out _, out _), Is.False);
        Assert.That(ArenaPackageStore.TryParseCandidateKey("", out _, out _), Is.False);
        Assert.That(ArenaPackageStore.PrefabGuidOf("g:minimum"), Is.EqualTo("g"));

        var frameCandidate = new ArenaPackageStore.Candidate("g:minimum", 2f, 3f);
        Assert.That(frameCandidate.prefabGuid, Is.EqualTo("g"));
        Assert.That(frameCandidate.frameId, Is.EqualTo("minimum"));
        Assert.That(frameCandidate.IsBaseFrame, Is.False);
        Assert.That(new ArenaPackageStore.Candidate("g", 2f, 3f).IsBaseFrame, Is.True);
    }

    [Test]
    public void FrameAttractionYieldsOneCandidatePerFrameAtItsOwnShrinkFootprint()
    {
        GameObject prefab = FramedAttraction(
            baseShrinkFeet: new Vector2(8.5f, 10.2f),
            Custom("fwide0001", "Wide", 30f, 20f, new Vector2(25.3f, 12.9f)),
            Minimum(6f, 4f, new Vector2(3.5f, 2.2f)));
        try
        {
            List<ArenaPackageStore.Candidate> candidates =
                ArenaPackageStore.CandidatesForPrefab("g", prefab);

            Assert.That(candidates.Select(c => c.guid),
                Is.EqualTo(new[] { "g:fwide0001", "g", "g:minimum" }), "ladder order");
            Assert.That(candidates.Select(c => (c.widthFeet, c.lengthFeet)),
                Is.EqualTo(new[] { (12, 25), (8, 10), (2, 3) }),
                "each frame's shrink, floored and rotation-normalized");
            Assert.That(candidates.Select(c => c.frameId),
                Is.EqualTo(new[] { "fwide0001", "base", "minimum" }));
            Assert.That(ArenaPackageStore.CandidateForPrefab("g", prefab).guid, Is.EqualTo("g"),
                "the single-candidate API still describes Base");

            ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, candidates);
            Assert.That(data.buckets.Select(b => (b.widthFeet, b.lengthFeet, b.guids.Single())),
                Is.EqualTo(new[] { (2, 3, "g:minimum"), (8, 10, "g"), (12, 25, "g:fwide0001") }));
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }
    }

    [Test]
    public void FrameLessAttractionsAndPropsKeepTheirSingleCandidate()
    {
        GameObject prefab = FramedAttraction(new Vector2(3.9f, 5.9f));
        try
        {
            List<ArenaPackageStore.Candidate> candidates =
                ArenaPackageStore.CandidatesForPrefab("plain", prefab);

            Assert.That(candidates.Count, Is.EqualTo(1));
            Assert.That(candidates[0].guid, Is.EqualTo("plain"));
            Assert.That((candidates[0].widthFeet, candidates[0].lengthFeet), Is.EqualTo((3, 5)));
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }

        GameObject propObject = new GameObject("Manual Prop");
        try
        {
            PropTemplate prop = propObject.AddComponent<PropTemplate>();
            prop.useColliderBounds = false;
            prop.customFootprintMeters = new Vector2(2f, 3f) * Ft;

            Assert.That(ArenaPackageStore.CandidatesForPrefab("prop", propObject).Select(c => c.guid),
                Is.EqualTo(new[] { "prop" }));
        }
        finally
        {
            Object.DestroyImmediate(propObject);
        }
    }

    [Test]
    public void SameBucketCandidatesOfOnePrefabKeepTheLargerAuthoredLayout()
    {
        // Wide (24 × 20 ft) shrinks into Base's 8 × 10 bucket and has more authored
        // area, so it replaces Base there. Minimum also lands in 8 × 10 but is
        // smaller than both, so it is dropped.
        GameObject prefab = FramedAttraction(
            baseShrinkFeet: new Vector2(8.5f, 10.2f),
            Custom("fwide0001", "Wide", 24f, 20f, new Vector2(8.9f, 10.9f)),
            Minimum(12f, 12f, new Vector2(8.1f, 10.1f)));
        try
        {
            List<ArenaPackageStore.Candidate> candidates =
                ArenaPackageStore.CandidatesForPrefab("g", prefab);

            Assert.That(candidates.Select(c => c.guid), Is.EqualTo(new[] { "g:fwide0001" }));
        }
        finally
        {
            Object.DestroyImmediate(prefab);
        }

        // Different prefabs sharing a bucket are never merged.
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, new[]
        {
            new ArenaPackageStore.Candidate("a", AttractionTemplate.BaseFrameId, 2f, 2f, 1f),
            new ArenaPackageStore.Candidate("b", AttractionTemplate.BaseFrameId, 2f, 2f, 9f),
            new ArenaPackageStore.Candidate("a", "fbig00001", 2f, 2f, 4f),
        });
        Assert.That(data.buckets.Single().guids, Is.EqualTo(new[] { "b", "a:fbig00001" }));
    }

    [Test]
    public void ReconcileDropsRemovedFrameKeysAndAppendsNewFrameCandidates()
    {
        var data = new ArenaPackageStore.Data
        {
            buckets = new List<ArenaPackageStore.Bucket>
            {
                new ArenaPackageStore.Bucket
                {
                    widthFeet = 2, lengthFeet = 3,
                    guids = new List<string> { "g:fgone0001", "coin", "g:minimum" },
                },
            },
            excludedGuids = new List<string> { "g:fgone0002" },
            sizeOverrides = new List<ArenaPackageStore.SizeOverride>
            {
                new ArenaPackageStore.SizeOverride { guid = "g:fgone0001", widthFeet = 1, lengthFeet = 1 },
            },
        };

        ArenaPackageStore.Reconcile(data, new[]
        {
            new ArenaPackageStore.Candidate("g", AttractionTemplate.BaseFrameId, 8f, 10f, 10f),
            new ArenaPackageStore.Candidate("g", "fnew00001", 2f, 3f, 3f),
            new ArenaPackageStore.Candidate("coin", 2f, 3f),
            new ArenaPackageStore.Candidate("g", "minimum", 2.5f, 3.5f, 1f),
        });

        Assert.That(data.buckets.Select(b => (b.widthFeet, b.lengthFeet)),
            Is.EqualTo(new[] { (2, 3), (8, 10) }));
        Assert.That(data.buckets[0].guids, Is.EqualTo(new[] { "coin", "g:fnew00001" }),
            "removed frames vanish; the new frame joins its natural bucket at the end; "
            + "Minimum loses its bucket to the larger new frame");
        Assert.That(data.buckets[1].guids, Is.EqualTo(new[] { "g" }));
        Assert.That(data.excludedGuids, Is.Empty);
        Assert.That(data.sizeOverrides, Is.Empty);
    }

    [Test]
    public void ExcludeMoveAndSizeOverrideWorkPerCandidateKey()
    {
        var candidates = new[]
        {
            new ArenaPackageStore.Candidate("g", AttractionTemplate.BaseFrameId, 8f, 10f, 10f),
            new ArenaPackageStore.Candidate("g", "minimum", 2f, 3f, 1f),
            new ArenaPackageStore.Candidate("coin", 1f, 1f),
        };
        ArenaPackageStore.Data data = ArenaPackageStore.Reconcile(null, candidates);

        Assert.That(ArenaPackageStore.MoveRelative(data, 2, 3, 1, 1, "g:minimum", "coin", false),
            Is.True);
        Assert.That(data.sizeOverrides.Single().guid, Is.EqualTo("g:minimum"));
        Assert.That(ArenaPackageStore.SetExcluded(data, candidates, "g", true), Is.True);
        string saved = JsonUtility.ToJson(data);
        data = ArenaPackageStore.Deserialize(saved);
        ArenaPackageStore.Reconcile(data, candidates);

        Assert.That(data.buckets.Single().guids, Is.EqualTo(new[] { "g:minimum", "coin" }));
        Assert.That(data.excludedGuids, Is.EqualTo(new[] { "g" }),
            "excluding Base leaves the frame candidate included");
        Assert.That(ArenaPackageStore.ClearSizeOverride(data, candidates, "g:minimum"), Is.True);
        Assert.That(data.buckets.Select(b => (b.widthFeet, b.lengthFeet, b.guids.Single())),
            Is.EqualTo(new[] { (1, 1, "coin"), (2, 3, "g:minimum") }));
    }

    private static GameObject FramedAttraction(Vector2 baseShrinkFeet,
        params AttractionLayoutFrame[] frames)
    {
        var root = new GameObject("A_Framed");
        AttractionTemplate attraction = root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.SetPackingBake(SyntheticBake(new Vector2(20f, 20f) * Ft, baseShrinkFeet * Ft));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>(frames));
        return root;
    }

    private static AttractionLayoutFrame Custom(string id, string name, float widthFeet,
        float lengthFeet, Vector2 shrinkFeet)
    {
        var frame = new AttractionLayoutFrame
        {
            id = id,
            name = name,
            kind = AttractionLayoutFrameKind.Custom,
            sizeFeet = new Vector2(widthFeet, lengthFeet),
        };
        frame.SetBake(SyntheticBake(frame.SizeMeters, shrinkFeet * Ft));
        return frame;
    }

    private static AttractionLayoutFrame Minimum(float widthFeet, float lengthFeet, Vector2 shrinkFeet)
    {
        AttractionLayoutFrame frame = Custom(AttractionTemplate.MinimumFrameId, "Minimum",
            widthFeet, lengthFeet, shrinkFeet);
        frame.kind = AttractionLayoutFrameKind.Minimum;
        return frame;
    }

    private static AttractionPackingBake SyntheticBake(Vector2 authored, Vector2 shrink) =>
        new AttractionPackingBake(authored, authored, shrink, authored * 1.25f, shrink,
            new List<AttractionPropPackingPose>());

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
