#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

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
        // ── Beta upload ids (Oct 9 2026) ────────────────────────────────────
        // A Beta upload builds "{id}Beta" from the one real folder "{id}". The
        // index used ManifestPath("{id}Beta"), a folder that does not exist, so
        // every Beta index shipped `packages: {}` (CrashCourseBeta v5: rootLogic
        // 108 / runtime 105 / shared 2, no bootstrap) and iOS fell back to every
        // -runtime bundle before PACK_PARK.
        private const string SourceId = "__DeliveryIndexBetaTests";
        private const string BetaId = SourceId + BetaContentId.Suffix;
        private const string Root = "Assets/Content/" + SourceId;

        private static DreamParkPackageManifest CreateSourceManifest()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Content")) AssetDatabase.CreateFolder("Assets", "Content");
            if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets/Content", SourceId);
            var manifest = ScriptableObject.CreateInstance<DreamParkPackageManifest>();
            // Compile(source, beta) stamps the Beta id; the asset stays in the source folder.
            manifest.contentId = BetaId;
            manifest.packageRevision = "sha256:test";
            manifest.arena = new PackageRecipe
            {
                kind = "arena",
                playerAddress = BetaId + "/Player",
                gameManagerAddress = BetaId + "/Game Container",
                occurrences = new List<PackageOccurrence>
                {
                    new PackageOccurrence { occurrenceId = "arena-a", resourceAddress = BetaId + "/Levels/Custom/A_Hero", role = "prop" },
                },
                arenaBuckets = new List<ArenaSizeBucket>
                {
                    new ArenaSizeBucket { widthFeet = 8, lengthFeet = 10, occurrenceIds = new List<string> { "arena-a" } },
                },
            };
            AssetDatabase.CreateAsset(manifest, DreamParkPackageCompiler.ManifestPath(SourceId));
            return manifest;
        }

        [TearDown]
        public void RemoveBetaFixture()
        {
            if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void BetaUploadIdResolvesTheSourceFolderManifest()
        {
            DreamParkPackageManifest manifest = CreateSourceManifest();

            Assert.That(AssetDatabase.IsValidFolder("Assets/Content/" + BetaId), Is.False,
                "a Beta upload never has a folder of its own");
            Assert.That(AssetDatabase.LoadAssetAtPath<DreamParkPackageManifest>(
                    DreamParkPackageCompiler.ManifestPath(BetaId)), Is.Null,
                "the pre-fix lookup (ManifestPath(betaId)) finds nothing; that was the bug");
            Assert.That(DreamParkPackageCompiler.SourceContentIdFor(BetaId), Is.EqualTo(SourceId));
            Assert.That(DreamParkPackageCompiler.SourceContentIdFor(SourceId), Is.EqualTo(SourceId));
            Assert.That(DreamParkPackageCompiler.ManifestPathForUpload(BetaId),
                Is.EqualTo(DreamParkPackageCompiler.ManifestPath(SourceId)),
                "SmartBundleGrouper moves THIS path into {betaId}-Bootstrap");
            Assert.That(DeliveryIndexGenerator.LoadPackageManifest(BetaId), Is.SameAs(manifest));
            Assert.That(DeliveryIndexGenerator.LoadPackageManifest(SourceId), Is.SameAs(manifest),
                "release uploads are unchanged");
        }

        [Test]
        public void UnknownIdsAreLeftAlone()
        {
            Assert.That(DreamParkPackageCompiler.SourceContentIdFor("__NoSuchTitleBeta"), Is.EqualTo("__NoSuchTitleBeta"));
            Assert.That(DreamParkPackageCompiler.SourceContentIdFor("__NoSuchTitle"), Is.EqualTo("__NoSuchTitle"));
            Assert.That(DeliveryIndexGenerator.LoadPackageManifest("__NoSuchTitleBeta"), Is.Null);
        }

        [Test]
        public void BetaIndexHasPackagesAndASmallBootstrapSet()
        {
            DreamParkPackageManifest manifest = CreateSourceManifest();
            string manifestAddress = DreamParkPackageManifest.AddressFor(BetaId);
            string bootstrapGroup = SmartBundleGrouper.BootstrapGroupName(BetaId);
            Assert.That(DeliveryIndexGenerator.RoleForGroup(BetaId, bootstrapGroup), Is.EqualTo("bootstrap"),
                "the group the grouper now moves the manifest into gets the bootstrap role");

            // Bundle graph shaped like a Beta build once the manifest is in its own group:
            // a tiny bootstrap bundle with no dependencies, next to heavy runtime shards.
            const string boot = "__deliveryindexbetatestsbeta-bootstrap_assets_all_0.bundle";
            const string player = "__deliveryindexbetatestsbeta-bundle-player_assets_all_0.bundle";
            const string manager = "__deliveryindexbetatestsbeta-runtime-7_assets_all_0.bundle";
            const string hero = "__deliveryindexbetatestsbeta-bundle-a_hero_assets_all_0.bundle";
            const string shard = "__deliveryindexbetatestsbeta-runtime-12_assets_all_0.bundle";
            var primary = new Dictionary<string, string>
            {
                [manifestAddress] = boot,
                [BetaId + "/Player"] = player,
                [BetaId + "/Game Container"] = manager,
                [BetaId + "/Levels/Custom/A_Hero"] = hero,
            };
            var deps = new Dictionary<string, IReadOnlyList<string>>
            {
                [boot] = new string[0],
                [player] = new[] { shard },
                [manager] = new[] { shard },
                [hero] = new[] { shard },
                [shard] = new string[0],
            };

            JSONObject packages = DeliveryIndexGenerator.BuildPackageHints(
                DeliveryIndexGenerator.LoadPackageManifest(BetaId), primary, deps);

            Assert.That(packages.keys, Is.Not.Empty, "packages must not be {} for a Beta upload");
            Assert.That(packages.GetField("manifestAddress").stringValue, Is.EqualTo(manifestAddress));
            Assert.That(packages.GetField("packageRevision").stringValue, Is.EqualTo("sha256:test"));
            Assert.That(packages.GetField("bootstrapBundles").list.Select(v => v.stringValue),
                Is.EqualTo(new[] { boot }), "the title bootstrap is the manifest bundle alone");
            JSONObject arena = packages.GetField("arena");
            Assert.That(arena, Is.Not.Null);
            Assert.That(arena.GetField("bootstrapBundles").list.Select(v => v.stringValue),
                Is.EquivalentTo(new[] { boot, player, manager, shard }));
            Assert.That(arena.GetField("startAddresses").list.Select(v => v.stringValue),
                Is.EqualTo(new[] { BetaId + "/Levels/Custom/A_Hero" }));
        }
    }
}
#endif
