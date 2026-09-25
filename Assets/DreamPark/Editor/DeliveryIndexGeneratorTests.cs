#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using NUnit.Framework;

namespace DreamPark.Editor
{
    public sealed class DeliveryIndexGeneratorTests
    {
        [TestCase("Game-Bootstrap", "bootstrap")]
        [TestCase("Game-Shared-Foundation", "foundation")]
        [TestCase("Game-Shared", "shared")]
        [TestCase("Game-Code", "code")]
        [TestCase("Game-Runtime", "runtime")]
        [TestCase("Game-Bundle-Foo-Content", "rootContent")]
        [TestCase("Game-Bundle-Foo", "rootLogic")]
        [TestCase("Game-Attractions", "content")]
        public void RoleForGroupIsStable(string groupName, string expected)
        {
            Assert.That(DeliveryIndexGenerator.RoleForGroup("Game", groupName), Is.EqualTo(expected));
        }

        [Test]
        public void TransitiveClosureIncludesRootAndEveryDependencyOnce()
        {
            var graph = new Dictionary<string, IReadOnlyList<string>>
            {
                ["hero.bundle"] = new[] { "runtime.bundle", "shared.bundle" },
                ["runtime.bundle"] = new[] { "foundation.bundle" },
                ["shared.bundle"] = new[] { "foundation.bundle" },
                ["foundation.bundle"] = new string[0],
            };

            Assert.That(DeliveryIndexGenerator.TransitiveClosure("hero.bundle", graph),
                Is.EqualTo(new[] { "foundation.bundle", "hero.bundle", "runtime.bundle", "shared.bundle" }));
        }

        [Test]
        public void ResolvePhysicalBundlePathPreservesUploadedSubdirectory()
        {
            string resolved = DeliveryIndexGenerator.ResolvePhysicalBundlePath(
                new[] { "https://cdn.example/iOS/title-models/models%20one/hero.bundle?hash=1" },
                new[] { "title-models/models one/hero.bundle", "other/hero.bundle" });

            Assert.That(resolved, Is.EqualTo("title-models/models one/hero.bundle"));
        }

        [Test]
        public void ResolvePhysicalBundlePathRejectsAmbiguousLeafOnlyLayout()
        {
            string resolved = DeliveryIndexGenerator.ResolvePhysicalBundlePath(
                new[] { "hero.bundle" },
                new[] { "title-models/hero.bundle", "title-textures/hero.bundle" });

            Assert.That(resolved, Is.Null);
        }
    }
}
#endif
