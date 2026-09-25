#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using DreamPark.ParkBuilder;
using NUnit.Framework;
using UnityEngine;

namespace DreamPark
{
    public sealed class DreamSequencePackageRuntimeTests
    {
        [Test]
        public void BuildKeepsManagerSeparateAndGivesStartAndEndTheirOwnFloors()
        {
            var definition = ScriptableObject.CreateInstance<DreamSequencePackageDefinition>();
            definition.startLevelPrefab = Special("Start");
            definition.overlayLevelPrefab = Special("Overlay");
            var effect = new GameObject("Default Level Transition");
            effect.transform.SetParent(definition.overlayLevelPrefab.transform, false);
            effect.AddComponent<ParticleSystem>();
            definition.gameOverLevelPrefab = Special("Game Over");
            definition.gameManagerPrefab = new GameObject("Game Manager Prefab");
            definition.minimumRoomMeters = new Vector2(1f, 1f);
            definition.maximumRoomMeters = new Vector2(6f, 8f);
            definition.levels = new List<DreamSequenceLevel>
            {
                new DreamSequenceLevel
                {
                    displayName = "First",
                    sourceGuid = "guid",
                    address = "SourceId/Levels/Custom/A_First",
                },
            };
            GameObject instance = null;
            try
            {
                instance = DreamSequencePackageRuntime.Create(definition, "RuntimeId", activate: false);
                Assert.That(instance, Is.Not.Null);
                DreamSequenceTemplate root = instance.GetComponent<DreamSequenceTemplate>();
                DreamParkPackageHost host = instance.GetComponent<DreamParkPackageHost>();
                DreamLevelLoader loader = instance.GetComponent<DreamLevelLoader>();
                Assert.That(root.generateFloor, Is.True);
                Assert.That(root.PackingBake.GrowFootprintMeters.x, Is.EqualTo(6f).Within(0.001f));
                Assert.That(root.PackingBake.GrowFootprintMeters.y, Is.EqualTo(8f).Within(0.001f));
                Assert.That(host.container.transform.parent, Is.EqualTo(instance.transform));
                Assert.That(host.container.GetComponent<OptimizedAFIgnore>(), Is.Not.Null);
                Assert.That(host.overlayLevel.transform.parent, Is.EqualTo(instance.transform));
                Assert.That(host.transitionEffect, Is.Not.Null);
                Assert.That(host.transitionEffect.transform.parent, Is.EqualTo(instance.transform));
                Assert.That(host.transitionEffect.GetComponent<ParticleSystem>(), Is.Not.Null);
                Assert.That(host.transitionEffect.GetComponent<OptimizedAFIgnore>(), Is.Not.Null);
                Assert.That(host.transitionEffect.activeSelf, Is.False);
                instance.transform.position = new Vector3(-105f, 0f, 4f);
                instance.SetActive(true);
                host.transitionEffect.SetActive(true);
                var registeredEffect = new LevelObject(host.transitionEffect);
                Assert.That(registeredEffect.renderers, Has.Length.EqualTo(1));
                Assert.That(registeredEffect.renderBounds.center,
                    Is.EqualTo(host.transitionEffect.transform.position));
                host.transitionEffect.SetActive(false);
                var noRenderer = new GameObject("No renderer");
                try
                {
                    noRenderer.transform.position = new Vector3(-105f, 0f, 4f);
                    var registeredEmpty = new LevelObject(noRenderer);
                    Assert.That(registeredEmpty.renderers, Is.Empty);
                    Assert.That(registeredEmpty.renderBounds.center,
                        Is.EqualTo(noRenderer.transform.position));
                }
                finally { Object.DestroyImmediate(noRenderer); }
                Assert.That(host.startLevel.transform.parent, Is.EqualTo(host.levelParent));
                Assert.That(host.gameOverLevel.transform.parent, Is.EqualTo(host.levelParent));
                Assert.That(host.startLevel.GetComponent<AttractionTemplate>().generateFloor, Is.True);
                Assert.That(host.overlayLevel.GetComponent<AttractionTemplate>().generateFloor, Is.False);
                Assert.That(host.gameOverLevel.GetComponent<AttractionTemplate>().generateFloor, Is.True);
                Assert.That(loader.levelAddresses, Is.EqualTo(new[]
                    { "RuntimeId/Levels/Custom/A_First" }));
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                Object.DestroyImmediate(definition.startLevelPrefab);
                Object.DestroyImmediate(definition.overlayLevelPrefab);
                Object.DestroyImmediate(definition.gameOverLevelPrefab);
                Object.DestroyImmediate(definition.gameManagerPrefab);
                Object.DestroyImmediate(definition);
            }
        }

        private static GameObject Special(string name)
        {
            var gameObject = new GameObject(name);
            var level = gameObject.AddComponent<AttractionTemplate>();
            level.size = GameLevelSize.Custom;
            level.customSize = new Vector2(12f, 18f);
            return gameObject;
        }
    }
}
#endif
