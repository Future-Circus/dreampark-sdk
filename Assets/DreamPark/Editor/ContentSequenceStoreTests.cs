#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using DreamPark.Editor;
using NUnit.Framework;

public sealed class ContentSequenceStoreTests
{
    [Test]
        public void MovingWorldBetweenAttractions_PreservesChildrenAndAttractionOrder()
        {
            ContentSequenceStore.Data data = Layout();
            ContentSequenceStore.Entry world = data.items[1];
            data.items.RemoveAt(1);
            data.items.Add(world);

            Assert.That(ContentSequenceStore.TryMove(data, "w:world", "top-before|a:d"), Is.True);

        Assert.That(data.items.Select(ContentSequenceStore.EntryToken),
            Is.EqualTo(new[] { "a:a", "w:world", "a:d" }));
        Assert.That(data.items[1].attractionGuids, Is.EqualTo(new[] { "b", "c" }));
        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "a", "b", "c", "d" }));
    }

    [Test]
    public void MovingWorldAfterAttraction_PreservesEveryAttraction()
    {
        ContentSequenceStore.Data data = Layout();

        Assert.That(ContentSequenceStore.TryMove(data, "w:world", "top-after|a:d"), Is.True);

        Assert.That(data.items.Select(ContentSequenceStore.EntryToken),
            Is.EqualTo(new[] { "a:a", "a:d", "w:world" }));
        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "a", "d", "b", "c" }));
    }

    [Test]
    public void AttractionCanBeInsertedAtExactPositionInsideWorld()
    {
        ContentSequenceStore.Data data = Layout();

        Assert.That(ContentSequenceStore.TryMove(data, "a:d", "world|world|1"), Is.True);

        ContentSequenceStore.Entry world = data.items.Single(x => x.IsWorld);
        Assert.That(world.attractionGuids, Is.EqualTo(new[] { "b", "d", "c" }));
        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "a", "b", "d", "c" }));
    }

    [Test]
    public void OutsideItemCanBeAppendedToWorld()
    {
        ContentSequenceStore.Data data = Layout();

        Assert.That(ContentSequenceStore.TryMove(data, "a:a", "world|world|2"), Is.True);

        Assert.That(data.items.Single(x => x.IsWorld).attractionGuids,
            Is.EqualTo(new[] { "b", "c", "a" }));
    }

    [Test]
    public void WorldItemCanBeMovedBetweenTwoWorlds()
    {
        ContentSequenceStore.Data data = Layout();
        data.items.Add(new ContentSequenceStore.Entry
        {
            kind = "world",
            id = "second-world",
            name = "Second World",
            attractionGuids = new List<string> { "e" },
        });

        Assert.That(ContentSequenceStore.TryMove(
            data, "a:b", "top-before|w:second-world"), Is.True);

        Assert.That(data.items.Select(ContentSequenceStore.EntryToken), Is.EqualTo(
            new[] { "a:a", "w:world", "a:d", "a:b", "w:second-world" }));
        Assert.That(data.items[1].attractionGuids, Is.EqualTo(new[] { "c" }));
    }

    [Test]
    public void SortingInsideWorldUsesExactDropPosition()
    {
        ContentSequenceStore.Data data = Layout();

        Assert.That(ContentSequenceStore.TryMove(data, "a:c", "world|world|0"), Is.True);

        Assert.That(data.items.Single(x => x.IsWorld).attractionGuids,
            Is.EqualTo(new[] { "c", "b" }));
    }

    [Test]
    public void MovingEarlierAttractionAfterLaterAnchor_SwapsOnFirstMove()
    {
        ContentSequenceStore.Data data = FlatLayout();

        Assert.That(ContentSequenceStore.TryMove(data, "a:a", "top-after|a:c"), Is.True);

        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "b", "c", "a" }));
    }

    [Test]
    public void MovingLaterAttractionBeforeEarlierAnchor_SwapsOnFirstMove()
    {
        ContentSequenceStore.Data data = FlatLayout();

        Assert.That(ContentSequenceStore.TryMove(data, "a:c", "top-before|a:a"), Is.True);

        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "c", "a", "b" }));
    }

    [Test]
    public void CloneCanBeReorderedWithoutMutatingDisplayedLayout()
    {
        ContentSequenceStore.Data displayed = FlatLayout();
        ContentSequenceStore.Data next = ContentSequenceStore.Clone(displayed);

        Assert.That(ContentSequenceStore.TryMove(next, "a:a", "top-after|a:c"), Is.True);

        Assert.That(ContentSequenceStore.Flatten(displayed), Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(ContentSequenceStore.Flatten(next), Is.EqualTo(new[] { "b", "c", "a" }));
    }

    [Test]
    public void ReconcilePreservesLiveWorldAndAttractionOrder()
    {
        ContentSequenceStore.Data live = Layout();
        Assert.That(ContentSequenceStore.TryMove(live, "w:world", "top-after|a:d"), Is.True);

        ContentSequenceStore.Data reconciled = ContentSequenceStore.Reconcile(
            live, new[] { "a", "b", "c", "d" });

        Assert.That(reconciled, Is.SameAs(live));
        Assert.That(reconciled.items.Select(ContentSequenceStore.EntryToken),
            Is.EqualTo(new[] { "a:a", "a:d", "w:world" }));
        Assert.That(ContentSequenceStore.Flatten(reconciled),
            Is.EqualTo(new[] { "a", "d", "b", "c" }));
    }

    [Test]
    public void ReconcileAddsNewItemsInCallerPriorityOrder()
    {
        ContentSequenceStore.Data data = new ContentSequenceStore.Data();

        ContentSequenceStore.Reconcile(data,
            new[] { "attraction-b", "attraction-a", "prop-b", "prop-a" });

        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(
            new[] { "attraction-b", "attraction-a", "prop-b", "prop-a" }));
    }

    [Test]
    public void ReconcileDoesNotUndoManualPropPlacement()
    {
        ContentSequenceStore.Data data = new ContentSequenceStore.Data
        {
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { attractionGuid = "prop" },
                new ContentSequenceStore.Entry { attractionGuid = "attraction" },
            },
        };

        ContentSequenceStore.Reconcile(data, new[] { "attraction", "prop" });

        Assert.That(ContentSequenceStore.Flatten(data),
            Is.EqualTo(new[] { "prop", "attraction" }));
    }

    [Test]
    public void HiddenAttractionMovesAfterActiveProgressionAndIsExcludedByDefault()
    {
        ContentSequenceStore.Data data = Layout();

        Assert.That(ContentSequenceStore.SetHidden(data, "b", true), Is.True);

        Assert.That(data.items.Last().attractionGuid, Is.EqualTo("b"));
        Assert.That(data.items.Last().hidden, Is.True);
        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "a", "c", "d" }));
        Assert.That(ContentSequenceStore.Flatten(data, includeHidden: true),
            Is.EqualTo(new[] { "a", "c", "d", "b" }));
    }

    [Test]
    public void HiddenGroupIsExcludedFromCompiledPackageButRetainedForEditing()
    {
        ContentSequenceStore.Data data = Layout();
        data.items.Single(item => item.IsWorld).hidden = true;

        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "a", "d" }));
        Assert.That(ContentSequenceStore.Flatten(data, includeHidden: true),
            Is.EqualTo(new[] { "a", "b", "c", "d" }));
    }

    [Test]
    public void UnhiddenAttractionReturnsToEndOfActiveProgression()
    {
        ContentSequenceStore.Data data = Layout();
        ContentSequenceStore.SetHidden(data, "b", true);

        Assert.That(ContentSequenceStore.SetHidden(data, "b", false), Is.True);

        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "a", "c", "d", "b" }));
        Assert.That(data.items.Last().hidden, Is.False);
    }

    [Test]
    public void ExplicitLayout_ProtectsEndpointsAndKeepsWorldsBetweenThem()
    {
        var data = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            startGuid = "start",
            endGuid = "end",
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { kind = "world", id = "world", name = "World" },
            },
            unsortedGuids = new List<string> { "loose" },
        };

        Assert.That(ContentSequenceStore.TryMove(data, "w:world", "unsorted-end"), Is.False);
        // Park Assets are copied into package slots, not moved out of the
        // explicit endpoint. Reusing Start as a later level is intentional.
        Assert.That(ContentSequenceStore.TryMove(data, "a:start", "top-end"), Is.True);
        Assert.That(data.startGuid, Is.EqualTo("start"));
        Assert.That(ContentSequenceStore.TryMove(data, "a:loose", "world|world|0"), Is.True);
        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "start", "loose", "start", "end" }));
    }

    [Test]
    public void ExplicitLayout_DroppingOnStartReplacesEndpointWithoutOwningUnusedAssets()
    {
        var data = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            startGuid = "old-start",
            endGuid = "end",
            unsortedGuids = new List<string> { "new-start" },
        };

        Assert.That(ContentSequenceStore.TryMove(data, "a:new-start", "start-slot"), Is.True);
        Assert.That(data.startGuid, Is.EqualTo("new-start"));
        Assert.That(data.unsortedGuids, Is.Empty);
    }

    [Test]
    public void MigrationWithoutWorlds_PutsOnlyEndpointsInProgression()
    {
        ContentSequenceStore.Data data = FlatLayout();

        ContentSequenceStore.MigrateToExplicitEndpoints(data, new[] { "a", "b", "c" }, false);

        Assert.That(data.startGuid, Is.EqualTo("a"));
        Assert.That(data.endGuid, Is.EqualTo("c"));
        Assert.That(data.items, Is.Empty);
        Assert.That(data.unsortedGuids, Is.EqualTo(new[] { "b" }));
    }

    [Test]
    public void MigrationWithOneAttraction_AllowsItToBeBothEndpoints()
    {
        var data = new ContentSequenceStore.Data
        {
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { attractionGuid = "only" },
            },
        };

        ContentSequenceStore.MigrateToExplicitEndpoints(data, new[] { "only" }, false);

        Assert.That(data.startGuid, Is.EqualTo("only"));
        Assert.That(data.endGuid, Is.EqualTo("only"));
        Assert.That(data.unsortedGuids, Is.Empty);
    }

    [Test]
    public void GroupCanMoveIntoAndOutOfHiddenSectionWithoutLosingChildren()
    {
        ContentSequenceStore.Data data = Layout();

        Assert.That(ContentSequenceStore.SetGroupHidden(data, "world", true), Is.True);
        Assert.That(data.items.Last().IsWorld, Is.True);
        Assert.That(data.items.Last().hidden, Is.True);
        Assert.That(data.items.Last().attractionGuids, Is.EqualTo(new[] { "b", "c" }));

        Assert.That(ContentSequenceStore.SetGroupHidden(data, "world", false), Is.True);
        Assert.That(data.items.Single(x => x.IsWorld).hidden, Is.False);
        Assert.That(data.items.Single(x => x.IsWorld).attractionGuids, Is.EqualTo(new[] { "b", "c" }));
    }

    [Test]
    public void RemovingGroupFromPackageLeavesChildrenInReusableLibraryOnly()
    {
        ContentSequenceStore.Data data = Layout();
        data.hasExplicitEndpoints = true;
        data.startGuid = "a";
        data.endGuid = "d";

        Assert.That(ContentSequenceStore.RemoveGroupFromPackage(data, "world"), Is.True);
        Assert.That(data.items.Any(x => x.IsWorld), Is.False);
        Assert.That(data.unsortedGuids, Is.Empty);
    }

    [Test]
    public void ExplicitPackage_CanPlaceTheSameLibraryAssetMoreThanOnce()
    {
        var data = new ContentSequenceStore.Data { hasExplicitEndpoints = true };

        Assert.That(ContentSequenceStore.TryMove(data, "a:repeat", "top-end"), Is.True);
        Assert.That(ContentSequenceStore.TryMove(data, "a:repeat", "top-end"), Is.True);

        Assert.That(data.items.Select(item => item.attractionGuid),
            Is.EqualTo(new[] { "repeat", "repeat" }));
        Assert.That(data.items.Select(item => item.id).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public void ExplicitPackage_ReconcilePreservesDuplicatePlacementsAndTheirOrder()
    {
        var data = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { id = "first", attractionGuid = "repeat" },
                new ContentSequenceStore.Entry { id = "second", attractionGuid = "other" },
                new ContentSequenceStore.Entry { id = "third", attractionGuid = "repeat" },
            },
        };

        ContentSequenceStore.Reconcile(data, new[] { "repeat", "other" });

        Assert.That(data.items.Select(ContentSequenceStore.PlacementToken),
            Is.EqualTo(new[] { "p:first", "p:second", "p:third" }));
        Assert.That(ContentSequenceStore.Flatten(data),
            Is.EqualTo(new[] { "repeat", "other", "repeat" }));
    }

    [Test]
    public void ExplicitPackage_ReconcileKeepsMissingReferencesForRepair()
    {
        var data = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            startGuid = "missing-start",
            endGuid = "live-end",
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry
                {
                    kind = "world", id = "group", name = "BlockLand",
                    attractionGuids = new List<string> { "live", "missing-child" },
                    attractionIds = new List<string> { "child-one", "child-two" },
                },
                new ContentSequenceStore.Entry { id = "missing-placement", attractionGuid = "missing-leaf" },
            },
        };

        ContentSequenceStore.Reconcile(data, new[] { "live", "live-end" });

        Assert.That(data.startGuid, Is.EqualTo("missing-start"));
        Assert.That(data.items[0].attractionGuids, Is.EqualTo(new[] { "live", "missing-child" }));
        Assert.That(data.items[0].attractionIds, Is.EqualTo(new[] { "child-one", "child-two" }));
        Assert.That(data.items[1].attractionGuid, Is.EqualTo("missing-leaf"));
    }

    [Test]
    public void ExplicitPackage_MovesOnlyTheDraggedPlacementInstance()
    {
        var data = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { id = "first", attractionGuid = "repeat" },
                new ContentSequenceStore.Entry { id = "middle", attractionGuid = "other" },
                new ContentSequenceStore.Entry { id = "last", attractionGuid = "repeat" },
            },
        };

        Assert.That(ContentSequenceStore.TryMove(data, "p:first", "top-after|p:last"), Is.True);

        Assert.That(data.items.Select(ContentSequenceStore.PlacementToken),
            Is.EqualTo(new[] { "p:middle", "p:last", "p:first" }));
    }

    [Test]
    public void ExplicitPackage_PlacementCanReplaceEndpointWithoutAffectingOtherCopies()
    {
        var data = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            startGuid = "old-start",
            endGuid = "end",
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { id = "replacement", attractionGuid = "repeat" },
                new ContentSequenceStore.Entry { id = "still-here", attractionGuid = "repeat" },
            },
        };

        Assert.That(ContentSequenceStore.TryMove(data, "p:replacement", "start-slot"), Is.True);

        Assert.That(data.startGuid, Is.EqualTo("repeat"));
        Assert.That(data.items.Select(ContentSequenceStore.PlacementToken),
            Is.EqualTo(new[] { "p:still-here" }));
    }

    [Test]
    public void CloneAndReconcileKeepPackageGroupSourceAndAllAvailableChildren()
    {
        var data = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry
                {
                    kind = "world",
                    id = "package-group",
                    sourceGroupId = "library-group",
                    name = "DesertLand",
                    attractionGuids = new List<string> { "attraction", "prop" },
                },
            },
        };

        ContentSequenceStore.Data copy = ContentSequenceStore.Clone(data);
        ContentSequenceStore.Reconcile(copy, new[] { "attraction", "prop" });

        ContentSequenceStore.Entry group = copy.items.Single();
        Assert.That(group.sourceGroupId, Is.EqualTo("library-group"));
        Assert.That(group.attractionGuids, Is.EqualTo(new[] { "attraction", "prop" }));
        Assert.That(group.attractionIds, Has.Count.EqualTo(2));
    }

    private static ContentSequenceStore.Data Layout()
    {
        return new ContentSequenceStore.Data
        {
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { attractionGuid = "a" },
                new ContentSequenceStore.Entry
                {
                    kind = "world",
                    id = "world",
                    name = "World",
                    attractionGuids = new List<string> { "b", "c" },
                },
                new ContentSequenceStore.Entry { attractionGuid = "d" },
            },
        };
    }

    private static ContentSequenceStore.Data FlatLayout()
    {
        return new ContentSequenceStore.Data
        {
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { attractionGuid = "a" },
                new ContentSequenceStore.Entry { attractionGuid = "b" },
                new ContentSequenceStore.Entry { attractionGuid = "c" },
            },
        };
    }
}
#endif
