#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace DreamPark.Editor
{
    /// Oct 9 2026: shared deps go to the bundle of exactly the set of roots
    /// that use them (SmartBundleGrouper.PlanDependencyOwnership). Pure: no
    /// AssetDatabase, no Addressables settings.
    public sealed class SmartBundleGrouperTests
    {
        private const string Game = "Game";
        private const long MB = 1024 * 1024;

        /// One build: the plan, and the layout the next build reads
        /// (placement + the root keys that loaded each group).
        private sealed class Build
        {
            public Dictionary<string, string> plan;
            public Dictionary<string, HashSet<string>> groupUsers;
        }

        private static Build Plan(
            Dictionary<string, HashSet<string>> graph, Dictionary<string, long> sizes = null,
            HashSet<string> logic = null, long small = MB, Build previous = null,
            HashSet<string> deleted = null, long cap = 4 * MB)
        {
            var roots = graph.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToList();
            var names = roots.ToDictionary(r => r, r => $"{Game}-Bundle-{r}");
            var keys = roots.ToDictionary(r => r, r => "guid-" + r);
            var plan = SmartBundleGrouper.PlanDependencyOwnership(Game, roots, graph, names, keys,
                d => logic != null && logic.Contains(d),
                d => sizes != null && sizes.TryGetValue(d, out long b) ? b : 2 * MB, small,
                previous?.plan, previous?.groupUsers, cap,
                d => deleted == null || !deleted.Contains(d));
            return new Build
            {
                plan = plan,
                groupUsers = SmartBundleGrouper.GroupUsersOf(plan, roots, graph, names, keys),
            };
        }

        private static Dictionary<string, HashSet<string>> Graph() => new Dictionary<string, HashSet<string>>
        {
            ["A"] = new HashSet<string> { "a1", "ab", "abc" },
            ["B"] = new HashSet<string> { "b1", "ab", "abc" },
            ["C"] = new HashSet<string> { "c1", "abc" },
        };

        /// Every group a root's load touches: its own two groups plus the
        /// group of each of its deps.
        private static HashSet<string> GroupsOf(string root, Dictionary<string, HashSet<string>> graph,
            Dictionary<string, string> plan)
        {
            var groups = new HashSet<string> { $"{Game}-Bundle-{root}", $"{Game}-Bundle-{root}-Content" };
            foreach (string dep in graph[root]) groups.Add(plan[dep]);
            return groups;
        }

        [Test]
        public void SharedSetNamesAreDeterministic()
        {
            string ab = SmartBundleGrouper.SharedSetGroupName(Game, new[] { "guid-A", "guid-B" });
            Assert.That(SmartBundleGrouper.SharedSetGroupName(Game, new[] { "GUID-B", "guid-a", "guid-b" }), Is.EqualTo(ab),
                "order, case and duplicates do not change the name");
            Assert.That(ab, Does.Match("^Game-SharedSet-[0-9a-f]{8}$"));
            Assert.That(SmartBundleGrouper.SharedSetGroupName(Game, new[] { "guid-A", "guid-C" }), Is.Not.EqualTo(ab));
            Assert.That(SmartBundleGrouper.SharedSetGroupName("GameBeta", new[] { "guid-A", "guid-B" }),
                Is.EqualTo("GameBeta" + ab.Substring(Game.Length)), "only the id prefix differs between Beta and release");
            Assert.That(Plan(Graph()).plan, Is.EqualTo(Plan(Graph()).plan), "the same input always gives the same plan");
        }

        [Test]
        public void DependencySharedByAAndBLandsInTheABBundle()
        {
            var plan = Plan(Graph()).plan;
            Assert.That(plan["ab"], Is.EqualTo(SmartBundleGrouper.SharedSetGroupName(Game, new[] { "guid-A", "guid-B" })));
            Assert.That(plan["abc"], Is.EqualTo(SmartBundleGrouper.SharedSetGroupName(Game, new[] { "guid-A", "guid-B", "guid-C" })));
            Assert.That(plan["a1"], Is.EqualTo("Game-Bundle-A-Content"));
            Assert.That(plan["b1"], Is.EqualTo("Game-Bundle-B-Content"));
            Assert.That(plan.ContainsKey("A"), Is.False, "roots stay in their own group");
        }

        [Test]
        public void AClosureNeverPullsBOnlyContent()
        {
            var graph = Graph();
            var plan = Plan(graph).plan;
            HashSet<string> aGroups = GroupsOf("A", graph, plan);
            foreach (var pair in plan.Where(pair => aGroups.Contains(pair.Value)))
                Assert.That(graph["A"], Does.Contain(pair.Key),
                    $"{pair.Key} is in a bundle A loads ({pair.Value}) but A never uses it");
            Assert.That(aGroups, Does.Not.Contain(plan["b1"]));
            Assert.That(aGroups, Does.Not.Contain(plan["c1"]));
        }

        [Test]
        public void RootPrefabIsAloneAndTinyRootsKeepOneContentBundle()
        {
            var plan = Plan(Graph(), logic: new HashSet<string> { "a1" }).plan;
            Assert.That(plan["a1"], Is.EqualTo("Game-Bundle-A-Content"),
                "a root with ≤ 10 MB of exclusive deps keeps logic and content together");
            Assert.That(plan.Values, Has.None.EqualTo("Game-Bundle-A"), "nothing joins a root prefab's own bundle");
        }

        [Test]
        public void LargeRootsSplitScriptBearingDepsIntoLogic()
        {
            var sizes = new Dictionary<string, long> { ["a1"] = 12 * MB };
            var graph = Graph();
            graph["A"].Add("a2");
            var plan = Plan(graph, sizes, logic: new HashSet<string> { "a2" }).plan;
            Assert.That(plan["a2"], Is.EqualTo("Game-Bundle-A-Logic"));
            Assert.That(plan["a1"], Is.EqualTo("Game-Bundle-A-Content"));
        }

        [Test]
        public void AddingRootsMovesOnlyWhatTheyShare()
        {
            var before = Plan(Graph()).plan;
            var unrelated = Graph();
            unrelated["D"] = new HashSet<string> { "d1" };
            var withD = Plan(unrelated).plan;
            foreach (var pair in before)
                Assert.That(withD[pair.Key], Is.EqualTo(pair.Value), "an unrelated root renames nothing: " + pair.Key);

            var sharing = Graph();
            sharing["E"] = new HashSet<string> { "a1" };
            var withE = Plan(sharing).plan;   // no layout file: plain set-keyed placement
            var moved = before.Where(pair => withE[pair.Key] != pair.Value).Select(pair => pair.Key).ToArray();
            Assert.That(moved, Is.EqualTo(new[] { "a1" }), "a root that shares a1 moves exactly a1");
            Assert.That(withE["a1"], Is.EqualTo(SmartBundleGrouper.SharedSetGroupName(Game, new[] { "guid-A", "guid-E" })));
        }

        [Test]
        public void SmallSharedSetsGoToOneBucket()
        {
            var sizes = new Dictionary<string, long> { ["ab"] = 100 * 1024, ["abc"] = 4 * MB };
            var plan = Plan(Graph(), sizes).plan;
            Assert.That(plan["ab"], Is.EqualTo(SmartBundleGrouper.SharedSmallGroupName(Game)),
                "a set under the small-set threshold joins SharedSet-Small");
            Assert.That(plan["abc"], Is.Not.EqualTo(SmartBundleGrouper.SharedSmallGroupName(Game)));
            Assert.That(DeliveryIndexGenerator.RoleForGroup(Game, plan["ab"]), Is.EqualTo("shared"));
            Assert.That(DeliveryIndexGenerator.RoleForGroup(Game, plan["abc"]), Is.EqualTo("shared"));
            Assert.That(DeliveryIndexGenerator.RoleForGroup(Game, plan["abc"] + "-2"), Is.EqualTo("shared"));
        }
        // ── Sticky placement (.dreampark-bundle-layout.json) ────────────────

        private static void AssertHomogeneous(Dictionary<string, HashSet<string>> graph, Dictionary<string, string> plan)
        {
            string UsersOf(string dep) => string.Join(",", graph.Where(r => r.Value.Contains(dep)).Select(r => r.Key).OrderBy(r => r));
            foreach (var bundle in plan.Where(p => p.Value.StartsWith("Game-SharedSet-") && p.Value != "Game-SharedSet-Small")
                         .GroupBy(p => p.Value))
                Assert.That(bundle.Select(p => UsersOf(p.Key)).Distinct().Count(), Is.EqualTo(1),
                    $"{bundle.Key} mixes user sets: " + string.Join(" | ", bundle.Select(p => p.Key + "=" + UsersOf(p.Key))));
        }

        private static Dictionary<string, HashSet<string>> TwoSharedAssets() => new Dictionary<string, HashSet<string>>
        {
            ["A"] = new HashSet<string> { "a1", "ab", "ab2" },
            ["B"] = new HashSet<string> { "b1", "ab", "ab2" },
        };

        [Test]
        public void SetKeyedPlansAreHomogeneous() => AssertHomogeneous(Graph(), Plan(Graph()).plan);

        [Test]
        public void ABundleWhoseMembersAllGainAUserKeepsItsName()
        {
            var graph = TwoSharedAssets();
            var first = Plan(graph);
            string x = first.plan["ab"];
            Assert.That(first.plan["ab2"], Is.EqualTo(x));
            graph["C"] = new HashSet<string> { "c1", "ab", "ab2" };      // a new root uses the whole bundle
            var sticky = Plan(graph, previous: first).plan;
            Assert.That(sticky["ab"], Is.EqualTo(x), "nothing to over-fetch: no re-ship");
            Assert.That(sticky["ab2"], Is.EqualTo(x));
            Assert.That(Plan(graph).plan["ab"], Is.Not.EqualTo(x), "without the layout it would have moved");
        }

        [Test]
        public void AGainWithinTheCapStays()
        {
            var graph = TwoSharedAssets();
            var sizes = new Dictionary<string, long> { ["ab"] = 2 * MB, ["ab2"] = 3 * MB };
            var first = Plan(graph, sizes);
            graph["C"] = new HashSet<string> { "c1", "ab" };            // C reuses ab only; would over-fetch ab2 (3 MB)
            var sticky = Plan(graph, sizes, previous: first).plan;
            Assert.That(sticky["ab"], Is.EqualTo(first.plan["ab"]), "3 MB over-fetch ≤ 4 MB cap: stays, nothing re-ships");
        }

        [Test]
        public void AGainOverTheCapMovesOnlyTheChangedAsset()
        {
            var graph = TwoSharedAssets();
            var sizes = new Dictionary<string, long> { ["ab"] = 2 * MB, ["ab2"] = 6 * MB };
            var first = Plan(graph, sizes);
            string x = first.plan["ab"];
            graph["C"] = new HashSet<string> { "c1", "ab" };            // staying would make C download 6 MB of ab2
            var sticky = Plan(graph, sizes, previous: first).plan;
            Assert.That(sticky["ab2"], Is.EqualTo(x), "the unchanged asset keeps the bundle");
            Assert.That(sticky["ab"], Is.EqualTo(SmartBundleGrouper.SharedSetGroupName(Game, new[] { "guid-A", "guid-B", "guid-C" })));
            Assert.That(GroupsOf("C", graph, sticky), Does.Not.Contain(x), "C never loads ab2's bundle");
        }

        [Test]
        public void AssetsThatLoseUsersStayPut()
        {
            var graph = Graph();
            var first = Plan(graph);
            graph.Remove("C");                                           // C deleted: abc → {A,B}; c1 has no user
            var sticky = Plan(graph, previous: first).plan;
            Assert.That(sticky["abc"], Is.EqualTo(first.plan["abc"]));
            Assert.That(sticky["ab"], Is.EqualTo(first.plan["ab"]));
            Assert.That(sticky["c1"], Is.EqualTo("Game-Bundle-C-Content"), "an orphan stays where it was (it may still be loaded by address)");
            var b = TwoSharedAssets();
            var two = Plan(b);
            b.Remove("B");                                              // ab, ab2 → one user each: still not moved
            var after = Plan(b, previous: two).plan;
            Assert.That(after["ab"], Is.EqualTo(two.plan["ab"]));
        }

        [Test]
        public void DeletedFilesAreNotKeptAsOrphans()
        {
            var graph = Graph();
            var first = Plan(graph);
            graph.Remove("C");
            var sticky = Plan(graph, previous: first, deleted: new HashSet<string> { "c1" }).plan;
            Assert.That(sticky.ContainsKey("c1"), Is.False);
        }

        [Test]
        public void NewAssetsNeverJoinAnExistingSharedBundle()
        {
            var graph = TwoSharedAssets();
            var first = Plan(graph);
            string x = first.plan["ab"];                                 // SharedSet for {A,B}
            graph["A"].Add("ab3");
            graph["B"].Add("ab3");                                      // a NEW asset also used by {A,B}
            var second = Plan(graph, previous: first).plan;
            Assert.That(second["ab"], Is.EqualTo(x));
            Assert.That(second["ab3"], Is.EqualTo(x + "-b"),
                "joining x would rewrite it (and every bundle that references it); a sibling bundle ships alone");
        }

        [Test]
        public void StickyPlanIsDeterministic()
        {
            var graph = Graph();
            var first = Plan(graph);
            graph.Remove("C");
            Assert.That(Plan(graph, previous: first).plan, Is.EqualTo(Plan(graph, previous: first).plan));
        }

        [Test]
        public void LayoutFileRoundTripsAndBadFilesFallBack()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dp-bundle-layout-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, ".dreampark-bundle-layout.json");
            try
            {
                Assert.That(SmartBundleGrouper.ReadBundleLayout(path).assets, Is.Empty, "missing file → empty");
                var layout = new SmartBundleGrouper.BundleLayout();
                layout.assets["0123abcd"] = "SharedSet-deadbeef";
                layout.roots["4567ef01"] = "Bundle-A_Hero";
                layout.groups["SharedSet-deadbeef"] = new List<string> { "4567ef01", "89ab" };
                Assert.That(SmartBundleGrouper.WriteBundleLayout(path, layout), Is.True);
                Assert.That(SmartBundleGrouper.WriteBundleLayout(path, layout), Is.False, "unchanged → not rewritten");
                var read = SmartBundleGrouper.ReadBundleLayout(path);
                Assert.That(read.assets, Is.EquivalentTo(layout.assets));
                Assert.That(read.roots, Is.EquivalentTo(layout.roots));
                Assert.That(read.groups["SharedSet-deadbeef"], Is.EquivalentTo(new[] { "4567ef01", "89ab" }));
                System.IO.File.WriteAllText(path, "{ not json");
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
                Assert.That(SmartBundleGrouper.ReadBundleLayout(path).assets, Is.Empty, "corrupt → empty → set-keyed");
                System.IO.File.WriteAllText(path, "{\"schemaVersion\":1,\"assets\":{},\"roots\":{}}");
                Assert.That(SmartBundleGrouper.ReadBundleLayout(path).roots, Is.Empty, "unknown schema → empty");
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
                System.IO.File.Delete(path);
                System.IO.Directory.Delete(dir);
            }
        }

        [Test]
        public void RootGroupNamesAreStickyAcrossRenames()
        {
            var roots = new List<string> { "Assets/Content/G/A_New.prefab", "Assets/Content/G/A_Other.prefab" };
            var previous = new Dictionary<string, string> { ["Assets/Content/G/A_New.prefab"] = "Game-Bundle-A_Old" };
            var names = SmartBundleGrouper.BuildRootGroupNames(Game, roots, previous);
            Assert.That(names["Assets/Content/G/A_New.prefab"], Is.EqualTo("Game-Bundle-A_Old"), "renamed root keeps its group");
            Assert.That(names["Assets/Content/G/A_Other.prefab"], Is.EqualTo("Game-Bundle-A_Other"));
            var clash = SmartBundleGrouper.BuildRootGroupNames(Game,
                new List<string> { "Assets/Content/G/A_New.prefab", "Assets/Content/G/A_Old.prefab" }, previous);
            Assert.That(clash["Assets/Content/G/A_Old.prefab"], Does.StartWith("Game-Bundle-A_Old-"),
                "a new root cannot take a name another root still holds");
            Assert.That(clash.Values.Distinct().Count(), Is.EqualTo(2));
            Assert.That(SmartBundleGrouper.BuildRootGroupNames(Game, roots), Is.EqualTo(new Dictionary<string, string>
            {
                ["Assets/Content/G/A_New.prefab"] = "Game-Bundle-A_New",
                ["Assets/Content/G/A_Other.prefab"] = "Game-Bundle-A_Other",
            }), "no layout → names from file names, as before");
        }
    }
    /// Oct 9 2026: the first upload after a bundle-layout change is a full upload.
    public sealed class BundleLayoutGateTests
    {
        [Test]
        public void LayoutIsVersionTwo() => Assert.That(SmartBundleGrouper.BundleLayoutVersion, Is.EqualTo(2));

        [Test]
        public void V1ParentUpgradesPatchToAllWithAReason()
        {
            Assert.That(BundleLayoutGate.Effective(UploadMode.Patch, 1, out string reason), Is.EqualTo(UploadMode.All));
            Assert.That(reason, Does.StartWith(BundleLayoutGate.Notice));
            Assert.That(reason, Does.Contain("v1").And.Contain("Patch"));
        }

        [Test]
        public void V1ParentUpgradesCodeOnlyToo()
        {
            Assert.That(BundleLayoutGate.Effective(UploadMode.CodeOnly, 1, out string reason), Is.EqualTo(UploadMode.All));
            Assert.That(reason, Is.Not.Null);
        }

        [Test]
        public void V2ParentKeepsPatch()
        {
            Assert.That(BundleLayoutGate.Effective(UploadMode.Patch, 2, out string reason), Is.EqualTo(UploadMode.Patch));
            Assert.That(reason, Is.Null);
            Assert.That(BundleLayoutGate.Effective(UploadMode.CodeOnly, 2, out _), Is.EqualTo(UploadMode.CodeOnly));
        }

        [Test]
        public void MissingStampMeansAll()
        {
            Assert.That(BundleLayoutGate.Effective(UploadMode.Patch, 0, out string reason), Is.EqualTo(UploadMode.All));
            Assert.That(reason, Does.Contain("unknown"));
            Assert.That(BuildManifestStore.ReadBundleLayoutVersion(new Defective.JSON.JSONObject("{\"schemaVersion\":2}")), Is.EqualTo(0));
            Assert.That(BuildManifestStore.ReadBundleLayoutVersion(new Defective.JSON.JSONObject("{\"bundleLayoutVersion\":2}")), Is.EqualTo(2));
            Assert.That(BuildManifestStore.ReadBundleLayoutVersion(null), Is.EqualTo(0));
            Assert.That(new BuildManifest().bundleLayoutVersion, Is.EqualTo(0), "an old saved baseline reads as unknown");
        }

        [Test]
        public void AllIsNeverChanged()
        {
            Assert.That(BundleLayoutGate.Effective(UploadMode.All, 0, out string reason), Is.EqualTo(UploadMode.All));
            Assert.That(reason, Is.Null);
        }
    }
}
#endif
