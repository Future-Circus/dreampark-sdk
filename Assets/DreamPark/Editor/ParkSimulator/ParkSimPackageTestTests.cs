#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Threading.Tasks;
using DreamPark;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    public sealed class ParkSimPackageTestTests
    {
        private static ContentEntry Attraction(string name) => new ContentEntry
        {
            displayName = name,
            kind = ContentKind.Attraction,
        };

        [Test]
        public void AdventureRouteKeepsEveryAttractionInOrderAndFacesPrevious()
        {
            var entries = new[] { Attraction("Start"), Attraction("Middle"), Attraction("End") };
            var markers = new[]
            {
                new SpawnPoint { markerName = "A", position = new Vector3(0f, 0f, 0f) },
                new SpawnPoint { markerName = "B", position = new Vector3(10f, 0f, 0f) },
                new SpawnPoint { markerName = "C", position = new Vector3(20f, 0f, 0f) },
            };

            List<SpawnPoint> stops = ParkSimPackageTest.PlaceAdventurePath(entries, markers,
                new List<string>());

            Assert.That(stops.Count, Is.EqualTo(3));
            Assert.That(stops[0].position.x, Is.EqualTo(0f).Within(0.01f));
            Assert.That(stops[1].position.x, Is.EqualTo(10f).Within(0.01f));
            Assert.That(stops[2].position.x, Is.EqualTo(20f).Within(0.01f));
            Assert.That(Vector3.Dot(stops[1].rotation * Vector3.forward, Vector3.left),
                Is.GreaterThan(0.99f));
            Assert.That(Vector3.Dot(stops[2].rotation * Vector3.forward, Vector3.left),
                Is.GreaterThan(0.99f));
        }

        [Test]
        public void PropsDecoratePreviousStopWithoutConsumingRoutePosition()
        {
            var entries = new[]
            {
                Attraction("Start"),
                new ContentEntry { displayName = "Sign", kind = ContentKind.Prop },
                Attraction("End"),
            };
            var markers = new[]
            {
                new SpawnPoint { markerName = "A", position = Vector3.zero },
                new SpawnPoint { markerName = "B", position = new Vector3(20f, 0f, 0f) },
            };

            List<SpawnPoint> stops = ParkSimPackageTest.PlaceAdventurePath(entries, markers,
                new List<string>());

            Assert.That(stops[0].position.x, Is.EqualTo(0f).Within(0.01f));
            Assert.That(stops[2].position.x, Is.EqualTo(20f).Within(0.01f));
            Assert.That(Vector3.Distance(stops[1].position, stops[0].position),
                Is.EqualTo(5f).Within(0.01f));
        }

        [Test]
        public void ArenaLayoutAppliesThePinnedFrameAndFallsBackToBase()
        {
            GameObject root = PackedAttraction("A_ArenaFrames", out Transform right);
            try
            {
                AttractionTemplate attraction = root.GetComponent<AttractionTemplate>();
                var minimum = new AttractionLayoutFrame
                {
                    id = AttractionTemplate.MinimumFrameId,
                    name = "Minimum",
                    kind = AttractionLayoutFrameKind.Minimum,
                    sizeFeet = new Vector2(10f, 10f),
                };
                minimum.overrides.Add(new AttractionLayoutOverride
                {
                    path = "Right", target = right, setActive = true, active = false,
                });
                attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { minimum });
                Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);
                Vector2 target = new Vector2(12f, 12f) * 0.3048f;

                Assert.That(ParkSimPackageTest.ApplyArenaLayout(root, target, "minimum"), Is.True);
                Assert.That(attraction.CurrentFrameId, Is.EqualTo("minimum"));
                Assert.That(right.gameObject.activeSelf, Is.False);
                Assert.That(attraction.RuntimeFootprintMeters.x,
                    Is.EqualTo(target.x).Within(0.001f), "12 ft is inside the Minimum frame's 10 → 12.5 ft range");

                Assert.That(ParkSimPackageTest.ApplyArenaLayout(root, target, "base"), Is.True);
                Assert.That(attraction.CurrentFrameId, Is.EqualTo(AttractionTemplate.BaseFrameId));
                Assert.That(right.gameObject.activeSelf, Is.True);

                ParkSimPackageTest.ApplyArenaLayout(root, target, "minimum");
                Assert.That(ParkSimPackageTest.ApplyArenaLayout(root, target, "fgone0001"), Is.True);
                Assert.That(attraction.CurrentFrameId, Is.EqualTo(AttractionTemplate.BaseFrameId),
                    "an unknown pinned frame falls back to Base");
                Assert.That(right.gameObject.activeSelf, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ArenaLayoutWithoutFramesMatchesTheLegacyPackingVariant()
        {
            GameObject framed = PackedAttraction("A_New", out _);
            GameObject legacy = PackedAttraction("A_Legacy", out _);
            try
            {
                AttractionTemplate a = framed.GetComponent<AttractionTemplate>();
                AttractionTemplate b = legacy.GetComponent<AttractionTemplate>();
                Assert.That(AttractionPackingBaker.Bake(a, false), Is.True);
                Assert.That(AttractionPackingBaker.Bake(b, false), Is.True);
                AttractionPackingBake bake = b.PackingBake;
                // Narrow along X so the Arena rectangle forces a 90° turn.
                Vector2 target = new Vector2(bake.ShrinkFootprintMeters.y,
                    bake.AuthoredFootprintMeters.x) * 1.01f;

                Assert.That(ParkSimPackageTest.ApplyArenaLayout(framed, target, null), Is.True);

                // The pre-frames ConfigureArenaInstance body, verbatim.
                bool rotate = ParkSimPackageTest.ShouldRotateArenaFootprint(bake.ShrinkFootprintMeters, target);
                legacy.transform.localRotation = rotate ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
                Vector2 localTarget = rotate ? new Vector2(target.y, target.x) : target;
                Vector2 authored = bake.AuthoredFootprintMeters;
                b.ApplyPackingVariant(new Vector2(localTarget.x / authored.x, localTarget.y / authored.y), false);

                Assert.That(framed.transform.localRotation, Is.EqualTo(legacy.transform.localRotation));
                Assert.That(a.RuntimeFootprintMeters, Is.EqualTo(b.RuntimeFootprintMeters));
                for (int i = 0; i < framed.transform.childCount; i++)
                    Assert.That(framed.transform.GetChild(i).localPosition,
                        Is.EqualTo(legacy.transform.GetChild(i).localPosition));
            }
            finally
            {
                Object.DestroyImmediate(framed);
                Object.DestroyImmediate(legacy);
            }
        }

        // 20 × 20 ft attraction with two 1 m props at x = ±1 m.
        private static GameObject PackedAttraction(string name, out Transform right)
        {
            var root = new GameObject(name);
            AttractionTemplate attraction = root.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = new Vector2(20f, 20f);
            ChildProp(root, "Left", -1f);
            right = ChildProp(root, "Right", 1f);
            return root;
        }

        private static Transform ChildProp(GameObject root, string name, float x)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(x, 0f, 0f);
            PropTemplate prop = go.AddComponent<PropTemplate>();
            prop.useColliderBounds = false;
            prop.customFootprintMeters = Vector2.one;
            return go.transform;
        }

        [Test]
        public async Task SequenceTestResolvesCompiledLevelFromLocalSourceGuid()
        {
            const string folder = "Assets/__ParkSimPackageTestTests";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "__ParkSimPackageTestTests");
            var source = new GameObject("Source Attraction");
            var package = new GameObject("Sequence");
            try
            {
                string path = folder + "/Source Attraction.prefab";
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(source, path);
                var sequence = package.AddComponent<DreamSequenceTemplate>();
                sequence.levels.Add(new DreamSequenceLevel
                {
                    sourceGuid = AssetDatabase.AssetPathToGUID(path),
                    displayName = "Source Attraction",
                });
                var loader = package.AddComponent<DreamLevelLoader>();
                loader.levelAddresses.Add("Test/Source Attraction");

                Assert.That(ParkSimPackageTest.ConfigureSequenceLoader(package), Is.True);
                Assert.That(await loader.LoadPrefabAsync(0), Is.SameAs(saved));
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(package);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
#endif
