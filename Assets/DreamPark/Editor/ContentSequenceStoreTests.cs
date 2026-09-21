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
    public void UnhiddenAttractionReturnsToEndOfActiveProgression()
    {
        ContentSequenceStore.Data data = Layout();
        ContentSequenceStore.SetHidden(data, "b", true);

        Assert.That(ContentSequenceStore.SetHidden(data, "b", false), Is.True);

        Assert.That(ContentSequenceStore.Flatten(data), Is.EqualTo(new[] { "a", "c", "d", "b" }));
        Assert.That(data.items.Last().hidden, Is.False);
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
