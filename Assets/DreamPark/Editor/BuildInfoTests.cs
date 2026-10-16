#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace DreamPark.Editor
{
    /// Oct 10 2026: buildInfo on every content version. Additive, and it never
    /// carries a path, user name, machine name or email.
    public sealed class BuildInfoTests
    {
        [Test]
        public void PersonalValuesAreNeverWritten()
        {
            Assert.That(BuildInfo.IsSafeValue("6000.3.23f1"), Is.True);
            Assert.That(BuildInfo.IsSafeValue("/Users/someone/project"), Is.False);
            Assert.That(BuildInfo.IsSafeValue("C:\\Users\\someone"), Is.False);
            Assert.That(BuildInfo.IsSafeValue("dev@example.com"), Is.False);
            if (Environment.UserName.Length >= 3)
                Assert.That(BuildInfo.IsSafeValue("built by " + Environment.UserName), Is.False);
            var info = new BuildInfo { unityVersion = "6000.3.23f1", contentCommit = "/Users/x/repo", modeForcedReason = "a@b.co" };
            string json = info.ToJson().ToString();
            Assert.That(json, Does.Contain("6000.3.23f1"));
            Assert.That(json, Does.Not.Contain("/Users/"));
            Assert.That(json, Does.Not.Contain("a@b.co"));
        }

        [Test]
        public void JsonCarriesVersionsSettingsAndCountsPerPlatform()
        {
            var info = new BuildInfo
            {
                unityVersion = "6000.3.23f1", sdkVersion = "1.8.0", bundleLayoutVersion = 2,
                contentCommit = new string('a', 40), contentDirty = true,
                uploadMode = "All", requestedUploadMode = "Patch", modeForcedReason = BundleLayoutGate.Notice,
                uploaderOrigin = "cli",
            };
            info.packages["com.unity.addressables"] = "2.3.16";
            info.platforms["iOS"] = new PlatformBuildInfo
            {
                buildTarget = "iOS", graphicsApis = new List<string> { "Metal" }, colorSpace = "Linear",
                contiguousBundles = true, nonRecursiveBuilding = true, crc = true, crcCachedBundles = true,
                compression = "LZ4", bundleCount = 3, totalBytes = 300,
            };
            info.platforms["Android"] = new PlatformBuildInfo { buildTarget = "Android", textureCompression = "ASTC" };
            var json = info.ToJson();
            Assert.That(json.GetField("bundleLayoutVersion").intValue, Is.EqualTo(2));
            Assert.That(json.GetField("contentDirty").boolValue, Is.True);
            Assert.That(json.GetField("packages").GetField("com.unity.addressables").stringValue, Is.EqualTo("2.3.16"));
            var ios = json.GetField("platforms").GetField("iOS");
            Assert.That(ios.GetField("contiguousBundles").boolValue, Is.True);
            Assert.That(ios.GetField("graphicsApis").list[0].stringValue, Is.EqualTo("Metal"));
            Assert.That(info.ToJson("iOS").GetField("platforms").HasField("Android"), Is.False,
                "a platform's delivery index carries only its own platform row");
        }

        [Test]
        public void TotalsSplitUploadedFromReused()
        {
            var current = new BuildManifest();
            var p = new BuildManifestPlatform { platform = "iOS" };
            p.files.Add(new BuildManifestFile { fileName = "a.bundle", sizeBytes = 100 });
            p.files.Add(new BuildManifestFile { fileName = "b.bundle", sizeBytes = 50 });
            p.files.Add(new BuildManifestFile { fileName = "catalog_x.json", sizeBytes = 999 });
            current.platforms.Add(p);
            var diff = new BuildManifestDiff();
            var pd = new PlatformDiff { platform = "iOS" };
            pd.changedFiles.Add("a.bundle");
            pd.changedFiles.Add("catalog_x.json");
            pd.unchangedFiles.Add("b.bundle");
            diff.platforms.Add(pd);

            var info = new BuildInfo();
            BuildInfoCollector.AddTotals(info, current, diff);
            Assert.That(info.bundleCount, Is.EqualTo(2));
            Assert.That(info.totalBytes, Is.EqualTo(150));
            Assert.That((info.bundlesUploaded, info.bytesUploaded), Is.EqualTo((1, 100L)));
            Assert.That((info.bundlesReused, info.bytesReused), Is.EqualTo((1, 50L)));

            var full = new BuildInfo();
            BuildInfoCollector.AddTotals(full, current, null);
            Assert.That(full.bundlesUploaded, Is.EqualTo(2), "no diff (full upload): everything is uploaded");
        }

        [Test]
        public void CollectReadsTheEditorWithoutPersonalData()
        {
            BuildInfo info = BuildInfoCollector.Collect("__NoSuchContent", "Patch", "Patch", null, automated: false);
            Assert.That(info.unityVersion, Is.EqualTo(UnityEngine.Application.unityVersion));
            Assert.That(info.uploaderOrigin, Is.EqualTo("gui"));
            Assert.That(info.ToJson().ToString(), Does.Not.Contain(Environment.UserName.Length >= 3 ? Environment.UserName : "\u0000"));
            Assert.That(info.contentCommit == null || System.Text.RegularExpressions.Regex.IsMatch(info.contentCommit, "^[0-9a-f]{40}$"));
        }
    }
}
#endif
