using System.Threading.Tasks;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using DreamPark;
using NUnit.Framework;
using UnityEngine;

public sealed class DreamParkPackageHostTests
{
    private GameObject firstRoot;
    private GameObject secondRoot;
    private GameObject sourcePrefab;

    [TearDown]
    public void TearDown()
    {
        if (firstRoot != null) Object.DestroyImmediate(firstRoot);
        if (secondRoot != null) Object.DestroyImmediate(secondRoot);
        if (sourcePrefab != null) Object.DestroyImmediate(sourcePrefab);
    }

    [Test]
    public void PackageAndContainerResolveFromCallerHierarchyWithoutGlobalSelection()
    {
        firstRoot = new GameObject("First Package");
        secondRoot = new GameObject("Second Package");
        var firstHost = firstRoot.AddComponent<DreamParkPackageHost>();
        var secondHost = secondRoot.AddComponent<DreamParkPackageHost>();
        secondHost.kind = DreamParkPackageKind.Adventure;
        firstHost.container = new GameObject("First Container");
        firstHost.container.transform.SetParent(firstRoot.transform);
        secondHost.container = new GameObject("Second Container");
        secondHost.container.transform.SetParent(secondRoot.transform);
        var firstLevel = new GameObject("First Level Script");
        firstLevel.transform.SetParent(firstRoot.transform);
        var secondLevel = new GameObject("Second Level Script");
        secondLevel.transform.SetParent(secondRoot.transform);

        Assert.That(DreamParkLuaAPI.Package(firstLevel), Is.SameAs(firstHost));
        Assert.That(DreamParkLuaAPI.Package(secondLevel), Is.SameAs(secondHost));
        Assert.That(DreamParkLuaAPI.ContainerObject(firstLevel), Is.SameAs(firstHost.container));
        Assert.That(DreamParkLuaAPI.ContainerObject(secondLevel), Is.SameAs(secondHost.container));
        Assert.That(DreamParkLuaAPI.IsSequence(firstLevel), Is.True);
        Assert.That(DreamParkLuaAPI.IsAdventure(firstLevel), Is.False);
        Assert.That(DreamParkLuaAPI.IsAdventure(secondLevel), Is.True);
        Assert.That(DreamParkLuaAPI.IsSequence(secondLevel), Is.False);
    }

    [Test]
    public void DetachedAdventureOccurrenceResolvesPackageThroughMembership()
    {
        firstRoot = new GameObject("Package Owner");
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        host.kind = DreamParkPackageKind.Adventure;
        host.adventureLevelNames = new List<string> { "Start", "A_Bricks", "A_Bricks", "End" };
        host.adventureLevelCount = host.adventureLevelNames.Count;
        secondRoot = new GameObject("Independent Level Anchor");
        var membership = secondRoot.AddComponent<DreamParkPackageMembership>();
        membership.host = host;
        membership.packageInstanceId = "package-one";
        membership.occurrenceId = "stage-two";
        var script = new GameObject("Attraction Script");
        script.transform.SetParent(secondRoot.transform);

        Assert.That(DreamParkLuaAPI.Package(script), Is.SameAs(host));
        Assert.That(DreamParkLuaAPI.IsAdventure(script), Is.True);
        Assert.That(DreamParkLuaAPI.LevelCount(script), Is.EqualTo(4));
        using (var levels = DreamParkLuaAPI.Levels(script))
        {
            Assert.That(levels.Get<int, string>(1), Is.EqualTo("Start"));
            Assert.That(levels.Get<int, string>(2), Is.EqualTo("A_Bricks"));
            Assert.That(levels.Get<int, string>(3), Is.EqualTo("A_Bricks"));
            Assert.That(levels.Get<int, string>(4), Is.EqualTo("End"));
        }
    }

    [Test]
    public void SequenceSlotsIncludeStartAndGameOverAroundStreamedAttractions()
    {
        firstRoot = new GameObject("Sequence");
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        var loader = firstRoot.AddComponent<DreamLevelLoader>();
        loader.levelAddresses.Add("Game/Levels/One");
        loader.levelAddresses.Add("Game/Levels/Two");
        host.levelLoader = loader;

        Assert.That(host.LevelCount, Is.EqualTo(4));
        Assert.That(host.CurrentLevelIndex, Is.EqualTo(1));
        Assert.That(DreamParkLuaAPI.LoadLevel(firstRoot, 0), Is.False);
        Assert.That(DreamParkLuaAPI.LoadLevel(firstRoot, 5), Is.False);
    }

    [Test]
    public void SequenceGroupIndicesResolveToAbsoluteSlotsWithoutChangingLegacyIndexing()
    {
        firstRoot = new GameObject("Sequence");
        var sequence = firstRoot.AddComponent<DreamSequenceTemplate>();
        sequence.levels = new List<DreamSequenceLevel> {
            new DreamSequenceLevel { displayName = "A", groupOccurrenceId = "group-a", sourceGroupId = "library-a", groupName = "First" },
            new DreamSequenceLevel { displayName = "B", groupOccurrenceId = "group-b", sourceGroupId = "library-b", groupName = "Second" },
            new DreamSequenceLevel { displayName = "C", groupOccurrenceId = "group-a", sourceGroupId = "library-a", groupName = "First" },
        };
        var loader = firstRoot.AddComponent<DreamLevelLoader>();
        loader.levelAddresses.AddRange(new[] { "A", "B", "C" });
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        host.levelLoader = loader;

        Assert.That(DreamParkLuaAPI.LevelCount(firstRoot), Is.EqualTo(5));
        Assert.That(DreamParkLuaAPI.LevelCount(firstRoot, "group-a"), Is.EqualTo(2));
        Assert.That(DreamParkLuaAPI.LevelCount(firstRoot, "library-a"), Is.EqualTo(2));
        Assert.That(DreamParkLuaAPI.LevelSlot(firstRoot, 0, "group-a"), Is.EqualTo(2));
        Assert.That(DreamParkLuaAPI.LevelReady(firstRoot, 0, "group-a"), Is.False);
        Assert.That(DreamParkLuaAPI.LevelSlot(firstRoot, 1, "library-a"), Is.EqualTo(4));
        Assert.That(DreamParkLuaAPI.LevelSlot(firstRoot, 1, "group-a"), Is.EqualTo(4));
        Assert.That(DreamParkLuaAPI.LevelSlot(firstRoot, 2, "group-a"), Is.Zero);
        Assert.That(DreamParkLuaAPI.LevelSlot(firstRoot, 0, "missing"), Is.Zero);
        Assert.That(DreamParkLuaAPI.LoadLevel(firstRoot, 0, "missing"), Is.False);
        using (var levels = DreamParkLuaAPI.Levels(firstRoot, "group-a"))
        {
            Assert.That(levels.Get<int, string>(1), Is.EqualTo("A"));
            Assert.That(levels.Get<int, string>(2), Is.EqualTo("C"));
        }
        using (var groups = DreamParkLuaAPI.Groups(firstRoot))
        {
            var first = groups.Get<int, XLua.LuaTable>(1);
            var second = groups.Get<int, XLua.LuaTable>(2);
            Assert.That(first.Get<string>("id"), Is.EqualTo("group-a"));
            Assert.That(first.Get<string>("sourceId"), Is.EqualTo("library-a"));
            Assert.That(first.Get<string>("name"), Is.EqualTo("First"));
            Assert.That(first.Get<int>("count"), Is.EqualTo(2));
            Assert.That(second.Get<string>("id"), Is.EqualTo("group-b"));
            Assert.That(second.Get<int>("count"), Is.EqualTo(1));
        }
        sequence.levels[1].sourceGroupId = "library-a";
        Assert.That(DreamParkLuaAPI.LevelCount(firstRoot, "library-a"), Is.Zero);
        Assert.That(DreamParkLuaAPI.LevelSlot(firstRoot, 0, "library-a"), Is.Zero);
        Assert.That(DreamParkLuaAPI.LevelCount(firstRoot, "group-a"), Is.EqualTo(2));
    }

    [Test]
    public void AdventureProgressionNeverHidesSpatialAttractions()
    {
        firstRoot = new GameObject("Adventure");
        var parent = new GameObject("Stops");
        parent.transform.SetParent(firstRoot.transform);
        var start = new GameObject("Start");
        start.transform.SetParent(parent.transform);
        var end = new GameObject("End");
        end.transform.SetParent(parent.transform);
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        host.kind = DreamParkPackageKind.Adventure;
        host.levelParent = parent.transform;

        Assert.That(host.LoadLevel(2), Is.True);
        Assert.That(host.CurrentLevelIndex, Is.EqualTo(2));
        Assert.That(start.activeSelf, Is.True);
        Assert.That(end.activeSelf, Is.True);
        Assert.That(host.ActivatePreparedLevel(1), Is.True);
        Assert.That(start.activeSelf && end.activeSelf, Is.True);
    }

    [Test]
    public void ArenaUsesSpatialProgressionAndLuaPackageIdentity()
    {
        firstRoot = new GameObject("Arena");
        var parent = new GameObject("Candidates");
        parent.transform.SetParent(firstRoot.transform);
        var first = new GameObject("First Candidate");
        first.transform.SetParent(parent.transform);
        var second = new GameObject("Second Candidate");
        second.transform.SetParent(parent.transform);
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        host.kind = DreamParkPackageKind.Arena;
        host.levelParent = parent.transform;
        host.adventureLevelNames = new List<string> { "First Candidate", "Second Candidate" };
        host.adventureLevelCount = 2;

        Assert.That(host.LoadLevel(2), Is.True);
        Assert.That(first.activeSelf && second.activeSelf, Is.True);
        Assert.That(DreamParkLuaAPI.IsArena(first), Is.True);
        Assert.That(DreamParkLuaAPI.IsAdventure(first), Is.False);
        Assert.That(DreamParkLuaAPI.IsSequence(first), Is.False);
        using (var levels = DreamParkLuaAPI.Levels(first))
        {
            Assert.That(levels.Get<int, string>(1), Is.EqualTo("First Candidate"));
            Assert.That(levels.Get<int, string>(2), Is.EqualTo("Second Candidate"));
        }
    }

    [Test]
    public async Task ConcurrentRequestsShareOneAddressLoadAndOneInstance()
    {
        firstRoot = new GameObject("Loader");
        sourcePrefab = new GameObject("Source Level");
        sourcePrefab.SetActive(false);
        var loader = firstRoot.AddComponent<DreamLevelLoader>();
        loader.levelAddresses.Add("Test/One");
        var gate = new UniTaskCompletionSource<GameObject>();
        int loadCalls = 0;
        loader.prefabProvider = _ =>
        {
            loadCalls++;
            return gate.Task;
        };

        Task<GameObject> first = loader.SpawnLevelAsync(0, false).AsTask();
        Task<GameObject> second = loader.SpawnLevelAsync(0, false).AsTask();
        Assert.That(loadCalls, Is.EqualTo(1));
        gate.TrySetResult(sourcePrefab);
        GameObject[] instances = await Task.WhenAll(first, second);

        Assert.That(instances[0], Is.SameAs(instances[1]));
        Assert.That(loader.GetInstance(0), Is.SameAs(instances[0]));
        Assert.That(loadCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task StreamedSequenceLevelEnablesItsOwnFloorBeforeActivation()
    {
        firstRoot = new GameObject("Sequence");
        sourcePrefab = new GameObject("Attraction Source");
        sourcePrefab.SetActive(false);
        var sourceTemplate = sourcePrefab.AddComponent<AttractionTemplate>();
        sourceTemplate.generateFloor = false;
        sourceTemplate.gridDensity = 7;
        var loader = firstRoot.AddComponent<DreamLevelLoader>();
        loader.levelAddresses.Add("Test/Attraction");
        loader.prefabProvider = _ => UniTask.FromResult(sourcePrefab);

        GameObject instance = await loader.SpawnLevelAsync(0, false);

        Assert.That(instance.GetComponent<AttractionTemplate>().generateFloor, Is.True);
        Assert.That(instance.GetComponent<AttractionTemplate>().gridDensity, Is.EqualTo(7));
        Assert.That(instance.activeSelf, Is.False);
        Assert.That(sourcePrefab.GetComponent<AttractionTemplate>().generateFloor, Is.False);
    }

    [Test]
    public async Task SequencePresentsPreparedLevelAndKeepsContainerThroughGameOverAndBack()
    {
        firstRoot = new GameObject("Sequence Runtime");
        var levels = new GameObject("Levels");
        levels.transform.SetParent(firstRoot.transform);
        var start = new GameObject("Start Level");
        start.transform.SetParent(levels.transform);
        var gameOver = new GameObject("Game Over Level");
        gameOver.transform.SetParent(levels.transform);
        gameOver.SetActive(false);
        var overlay = new GameObject("Overlay Level");
        overlay.transform.SetParent(firstRoot.transform);
        overlay.SetActive(false);
        var container = new GameObject("Game Container");
        container.transform.SetParent(firstRoot.transform);
        sourcePrefab = new GameObject("Streamed Attraction Source");
        sourcePrefab.SetActive(false);

        var loader = firstRoot.AddComponent<DreamLevelLoader>();
        loader.levelAddresses.Add("Test/Attraction");
        loader.levelParent = levels.transform;
        loader.prefabProvider = _ => UniTask.FromResult(sourcePrefab);
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        host.kind = DreamParkPackageKind.Sequence;
        host.container = container;
        host.levelParent = levels.transform;
        host.startLevel = start;
        host.gameOverLevel = gameOver;
        host.overlayLevel = overlay;
        host.levelLoader = loader;

        Assert.That(start.activeSelf, Is.True);
        Assert.That(overlay.activeSelf, Is.False);
        Assert.That(DreamParkLuaAPI.ContainerObject(start), Is.SameAs(container));

        GameObject firstInstance = await loader.SpawnLevelAsync(0, false);
        Assert.That(host.ActivatePreparedLevel(2), Is.True);
        Assert.That(host.CurrentLevelIndex, Is.EqualTo(2));
        Assert.That(start.activeSelf, Is.False);
        Assert.That(overlay.activeSelf, Is.True);
        Assert.That(firstInstance.activeSelf, Is.True);

        Assert.That(host.ActivatePreparedLevel(3), Is.True);
        Assert.That(host.CurrentLevelIndex, Is.EqualTo(3));
        Assert.That(gameOver.activeSelf, Is.True);
        Assert.That(overlay.activeSelf, Is.False);
        Assert.That(container.activeSelf, Is.True);
        Assert.That(loader.GetInstance(0), Is.Null);

        await loader.SpawnLevelAsync(0, false);
        Assert.That(host.ActivatePreparedLevel(2), Is.True);
        Assert.That(host.CurrentLevelIndex, Is.EqualTo(2));
        Assert.That(overlay.activeSelf, Is.True);
        Assert.That(container.activeSelf, Is.True);
    }

    [Test]
    public void SequenceRoomFitNeverScalesAWholeAttractionPastItsBakedMaximum()
    {
        firstRoot = new GameObject("Sequence");
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        var level = new GameObject("Small Attraction");
        level.transform.SetParent(firstRoot.transform, false);
        level.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
        var attraction = level.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(2f, 2f) / 0.3048f;
        attraction.maxGrowthScale = new Vector2(1.25f, 1.25f);
        attraction.SetPackingBake(new AttractionPackingBake(
            new Vector2(2f, 2f), new Vector2(2f, 2f),
            new Vector2(1f, 1f), new Vector2(2.5f, 2.5f),
            new Vector2(1f, 1f), new List<AttractionPropPackingPose>()));
        host.startLevel = level;

        host.SetRoomFootprintMeters(new Vector2(5f, 5f));

        Assert.That(level.transform.localScale, Is.EqualTo(new Vector3(0.8f, 1f, 0.8f)));
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(2.5f).Within(0.001f));
        Assert.That(attraction.RuntimeFootprintMeters.y, Is.EqualTo(2.5f).Within(0.001f));
    }

    [Test]
    public void SequenceFloorFillsPackedRoomWhileSmallLevelKeepsItsOwnBakeAndCutout()
    {
        firstRoot = new GameObject("Sequence");
        var sequence = firstRoot.AddComponent<DreamSequenceTemplate>();
        sequence.size = GameLevelSize.Custom;
        sequence.customSize = new Vector2(4f, 6f) / 0.3048f;
        sequence.maxGrowthScale = new Vector2(1.25f, 7f / 6f);
        sequence.gridDensity = 8;
        sequence.SetPackingBake(new AttractionPackingBake(
            new Vector2(4f, 6f), new Vector2(4f, 6f),
            new Vector2(3f, 5f), new Vector2(5f, 7f),
            new Vector2(3f, 5f), new List<AttractionPropPackingPose>()));
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        var level = new GameObject("Small Level");
        level.transform.SetParent(firstRoot.transform, false);
        var attraction = level.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(2f, 2f) / 0.3048f;
        attraction.maxGrowthScale = new Vector2(1.25f, 1.25f);
        attraction.gridDensity = 3;
        attraction.SetPackingBake(new AttractionPackingBake(
            new Vector2(2f, 2f), new Vector2(2f, 2f),
            new Vector2(1f, 1f), new Vector2(2.5f, 2.5f),
            new Vector2(1f, 1f), new List<AttractionPropPackingPose>()));
        var pit = new GameObject("Pit");
        pit.transform.SetParent(level.transform, false);
        var cutout = pit.AddComponent<FloorCutout>();
        cutout.points.Add(new Vector3(-0.4f, 0f, -0.4f));
        cutout.points.Add(new Vector3(0.4f, 0f, -0.4f));
        cutout.points.Add(new Vector3(0.4f, 0f, 0.4f));
        cutout.points.Add(new Vector3(-0.4f, 0f, 0.4f));
        host.startLevel = level;

        host.SetRoomFootprintMeters(new Vector2(4f, 6f));
        attraction.RegenerateFloor();
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(2.5f).Within(0.001f));
        Assert.That(attraction.gridDensity, Is.EqualTo(3));
        Assert.That(attraction.gridWidth, Is.EqualTo(4f).Within(0.001f));
        Assert.That(attraction.gridHeight, Is.EqualTo(6f).Within(0.001f));
        Assert.That(attraction.gridX, Is.EqualTo(8));
        Assert.That(attraction.runtimePlane.GetComponent<MeshFilter>().sharedMesh.triangles.Length,
            Is.LessThan(attraction.gridX * attraction.gridY * 6));

        host.SetRoomFootprintMeters(new Vector2(5f, 7f));
        Assert.That(sequence.RuntimeFootprintMeters.x, Is.EqualTo(5f).Within(0.001f));
        Assert.That(attraction.gridWidth, Is.EqualTo(5f).Within(0.001f));
        Assert.That(attraction.gridHeight, Is.EqualTo(7f).Within(0.001f));
        Assert.That(attraction.RuntimeFootprintMeters.x, Is.EqualTo(2.5f).Within(0.001f));
    }

    [Test]
    public void SequenceAnchorsBindToLevelFloorNotItsCalibrationReference()
    {
        firstRoot = new GameObject("Sequence");
        var owner = firstRoot.AddComponent<AttractionTemplate>();
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        var floor = new GameObject("LevelFloor");
        floor.transform.SetParent(firstRoot.transform, false);
        var filter = floor.AddComponent<MeshFilter>();
        var mesh = new Mesh();
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward };
        mesh.triangles = new[] { 0, 1, 2 };
        filter.sharedMesh = mesh;
        var calibration = floor.AddComponent<CalibrateLevel>();
        owner.runtimePlane = floor;
        var level = new GameObject("Start");
        level.transform.SetParent(firstRoot.transform, false);
        var levelTemplate = level.AddComponent<AttractionTemplate>();
        var levelFloor = new GameObject("Start Floor");
        levelFloor.transform.SetParent(level.transform, false);
        var levelFilter = levelFloor.AddComponent<MeshFilter>();
        levelFilter.sharedMesh = mesh;
        var levelCalibration = levelFloor.AddComponent<CalibrateLevel>();
        levelTemplate.runtimePlane = levelFloor;
        var prop = new GameObject("Floor-bound prop");
        prop.transform.SetParent(level.transform, false);
        var anchor = prop.AddComponent<FloorAnchor>();
        host.startLevel = level;

        host.SetRoomFootprintMeters(new Vector2(3f, 4f));

        Assert.That(anchor.floorMeshFilter, Is.SameAs(levelFilter));
        Assert.That(anchor.calibrator, Is.SameAs(levelCalibration));
        Assert.That(anchor.floorMeshFilter, Is.Not.SameAs(filter));
        Assert.That(anchor.calibrator, Is.Not.SameAs(calibration));
    }

    [Test]
    public void SequenceFloorNeverAnchorsItsStructuralLevelsContainer()
    {
        firstRoot = new GameObject("Sequence");
        var sequence = firstRoot.AddComponent<DreamSequenceTemplate>();
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        var levels = new GameObject("Levels");
        levels.transform.SetParent(firstRoot.transform, false);
        host.levelParent = levels.transform;

        sequence.RegenerateFloor();

        Assert.That(sequence.runtimePlane, Is.Not.Null);
        Assert.That(levels.GetComponent<FloorAnchor>(), Is.Null);
    }

    [Test]
    public void SequenceCalibrationReferenceDoesNotInheritAnAttractionsPit()
    {
        firstRoot = new GameObject("Sequence");
        var sequence = firstRoot.AddComponent<DreamSequenceTemplate>();
        firstRoot.AddComponent<DreamParkPackageHost>();
        var level = new GameObject("Lava Level");
        level.transform.SetParent(firstRoot.transform, false);
        var attraction = level.AddComponent<AttractionTemplate>();
        var pit = new GameObject("Lava Pit");
        pit.transform.SetParent(level.transform, false);
        var cutout = pit.AddComponent<FloorCutout>();
        cutout.points.Add(new Vector3(-0.4f, 0f, -0.4f));
        cutout.points.Add(new Vector3(0.4f, 0f, -0.4f));
        cutout.points.Add(new Vector3(0.4f, 0f, 0.4f));
        cutout.points.Add(new Vector3(-0.4f, 0f, 0.4f));

        sequence.RegenerateFloor();
        attraction.RegenerateFloor();

        int referenceTriangles = sequence.runtimePlane.GetComponent<MeshFilter>()
            .sharedMesh.triangles.Length;
        int attractionTriangles = attraction.runtimePlane.GetComponent<MeshFilter>()
            .sharedMesh.triangles.Length;
        Assert.That(referenceTriangles, Is.EqualTo(sequence.gridX * sequence.gridY * 6));
        Assert.That(attractionTriangles, Is.LessThan(attraction.gridX * attraction.gridY * 6));
    }

    [Test]
    public void SequenceProjectsSavedGradeOntoAChildFloorWithDifferentTopology()
    {
        firstRoot = new GameObject("Sequence");
        var referenceOwner = firstRoot.AddComponent<DreamSequenceTemplate>();
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        var reference = new GameObject("Calibration Reference");
        reference.transform.SetParent(firstRoot.transform, false);
        var referenceMesh = new Mesh();
        referenceMesh.vertices = new[] {
            new Vector3(-1f, 0.1f, -1f), new Vector3(1f, 0.2f, -1f),
            new Vector3(-1f, 0.3f, 1f), new Vector3(1f, 0.4f, 1f)
        };
        referenceMesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        reference.AddComponent<MeshFilter>().sharedMesh = referenceMesh;
        var referenceCollider = reference.AddComponent<MeshCollider>();
        referenceCollider.sharedMesh = referenceMesh;
        reference.AddComponent<CalibrateLevel>().calibrated = true;
        referenceOwner.runtimePlane = reference;

        var level = new GameObject("Attraction With Pit");
        level.transform.SetParent(firstRoot.transform, false);
        var levelOwner = level.AddComponent<AttractionTemplate>();
        var floor = new GameObject("Custom Floor");
        floor.transform.SetParent(level.transform, false);
        var customMesh = new Mesh();
        customMesh.vertices = new[] {
            new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f),
            new Vector3(-1f, 0f, 1f), new Vector3(1f, 0f, 1f),
            new Vector3(0f, 0f, 0f)
        };
        // Deliberately omit one side of the floor, as a cutout does.
        int[] triangles = { 0, 2, 4, 0, 4, 1 };
        customMesh.triangles = triangles;
        floor.AddComponent<MeshFilter>().sharedMesh = customMesh;
        floor.AddComponent<MeshCollider>().sharedMesh = customMesh;
        floor.AddComponent<CalibrateLevel>();
        levelOwner.runtimePlane = floor;
        host.startLevel = level;

        host.SetRoomFootprintMeters(new Vector2(3f, 4f));

        Assert.That(referenceCollider.enabled, Is.False);
        Assert.That(customMesh.triangles, Is.EqualTo(triangles));
        Assert.That(customMesh.vertices[4].y, Is.GreaterThan(0.1f));
        Assert.That(floor.GetComponent<CalibrateLevel>().calibrated, Is.True);
    }

    [Test]
    public void NestedSinglePointAnchorUsesItsOwnParentSpaceWhenFollowingGrade()
    {
        firstRoot = new GameObject("Sequence");
        var floor = new GameObject("LevelFloor");
        floor.transform.SetParent(firstRoot.transform, false);
        var filter = floor.AddComponent<MeshFilter>();
        var mesh = new Mesh();
        mesh.vertices = new[] {
            new Vector3(0f, 0f, -1f), new Vector3(2f, 0f, -1f),
            new Vector3(0f, 0f, 1f)
        };
        mesh.triangles = new[] { 0, 1, 2 };
        filter.sharedMesh = mesh;
        var calibration = floor.AddComponent<CalibrateLevel>();
        calibration.calibrated = true;
        var level = new GameObject("Rotated attraction");
        level.transform.SetParent(firstRoot.transform, false);
        level.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
        var prop = new GameObject("Prop");
        prop.transform.SetParent(level.transform, false);
        prop.transform.localPosition = Vector3.right;
        var anchor = prop.AddComponent<FloorAnchor>();
        anchor.BindToFloor(filter, calibration);

        Vector3[] vertices = mesh.vertices;
        vertices[0].y = 0.5f;
        mesh.vertices = vertices;
        anchor.Update();

        Assert.That(prop.transform.position.x, Is.EqualTo(0f).Within(0.001f));
        Assert.That(prop.transform.position.y, Is.EqualTo(0.5f).Within(0.001f));
        Assert.That(prop.transform.position.z, Is.EqualTo(-1f).Within(0.001f));

        // Repacking a level recaches X/Z samples after the prop moves. That
        // must not bake the already-applied grade into its authored Y offset.
        anchor.RecacheCorners();
        vertices[0].y = 0.75f;
        mesh.vertices = vertices;
        anchor.Update();
        Assert.That(prop.transform.position.y, Is.EqualTo(0.75f).Within(0.001f));
    }

    [Test]
    public void TransitionEffectCanBeStartedAgainAfterItStops()
    {
        firstRoot = new GameObject("Sequence");
        var host = firstRoot.AddComponent<DreamParkPackageHost>();
        var effect = new GameObject("Transition");
        effect.transform.SetParent(firstRoot.transform, false);
        effect.AddComponent<ParticleSystem>();
        effect.SetActive(false);
        host.transitionEffect = effect;
        var play = typeof(DreamParkPackageHost).GetMethod("PlayTransition",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var stop = typeof(DreamParkPackageHost).GetMethod("StopTransition",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        play.Invoke(host, null);
        Assert.That(effect.activeSelf, Is.True);
        stop.Invoke(host, null);
        Assert.That(effect.activeSelf, Is.False);
        play.Invoke(host, null);
        Assert.That(effect.activeSelf, Is.True);
    }

    [Test]
    public void GradeSnapshotSurvivesDestructionAndRestoresNonFlatReplacementMesh()
    {
        firstRoot = new GameObject("Graded Floor Test");
        var previous = new GameObject("Old Floor");
        previous.transform.SetParent(firstRoot.transform);
        var oldMesh = new Mesh();
        oldMesh.vertices = new[] {
            new Vector3(-1f, 0.2f, -1f), new Vector3(1f, 0.4f, -1f),
            new Vector3(-1f, 0.6f, 1f), new Vector3(1f, 0.8f, 1f)
        };
        oldMesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        previous.AddComponent<MeshFilter>().sharedMesh = oldMesh;
        previous.AddComponent<MeshCollider>().sharedMesh = oldMesh;
        var oldCalibration = previous.AddComponent<CalibrateLevel>();
        oldCalibration.calibrated = true;
        Vector3[] grade = oldCalibration.CaptureGradeWorldSamples();
        Object.DestroyImmediate(previous);

        var replacement = new GameObject("Replacement Floor");
        replacement.transform.SetParent(firstRoot.transform);
        var newMesh = new Mesh();
        newMesh.vertices = new[] {
            new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
            new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f)
        };
        newMesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        replacement.AddComponent<MeshFilter>().sharedMesh = newMesh;
        replacement.AddComponent<MeshCollider>().sharedMesh = newMesh;
        var calibration = replacement.AddComponent<CalibrateLevel>();

        Assert.That(calibration.TransferGradeFrom(grade), Is.True);
        Vector3[] restored = newMesh.vertices;
        Assert.That(calibration.calibrated, Is.True);
        Assert.That(restored[0].y, Is.GreaterThan(0.01f));
        Assert.That(restored[3].y, Is.GreaterThan(restored[0].y));
    }
}
