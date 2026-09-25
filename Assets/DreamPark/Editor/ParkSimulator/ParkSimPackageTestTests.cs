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
