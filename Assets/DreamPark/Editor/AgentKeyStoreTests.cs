using System;
using System.IO;
using DreamPark.API;
using NUnit.Framework;

// Agent keys: one per title (owner, 2026-10-09). A multi-title project (creaturez: BirthdayBash,
// LaserTag, Zombiez, LayoutLab) kept ONE Library/DreamParkAgent/agent-key.json, and every
// approval overwrote it with an identity on that title's team only, so publishing title A after
// approving B failed with "This agent isn't on A's team". The store now keeps one entry per
// approval and content-scoped work picks the entry for its title.
//
// Every test runs against a temp project root (AgentKeyStore.UseProjectRootForTests); the
// project's real Library/DreamParkAgent is never read or written. The keys are fake.
public sealed class AgentKeyStoreTests
{
    private string _root;
    private IDisposable _rootOverride;

    private static long FutureMs => DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
    private static long PastMs => DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

    private string KeyDir => Path.Combine(_root, "Library", "DreamParkAgent");

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "dp-agentkeys-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        Directory.CreateDirectory(_root);
        _rootOverride = AgentKeyStore.UseProjectRootForTests(_root);
    }

    [TearDown]
    public void TearDown()
    {
        _rootOverride?.Dispose();
        _rootOverride = null;
        // Only the temp folder this test created.
        try { if (_root != null && _root.StartsWith(Path.GetTempPath(), StringComparison.Ordinal) && Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (Exception) { }
    }

    private static AgentKeyRecord Key(string suffix, string contentId, long expiresAt = 0)
    {
        return new AgentKeyRecord
        {
            agentKey = "agk_test_" + suffix,
            keyId = "key_" + suffix,
            uid = "uid_" + suffix,
            label = "test",
            contentId = contentId,
            expiresAt = expiresAt == 0 ? FutureMs : expiresAt,
            scopes = new[] { "content" },
        };
    }

    // Compare keys without ever putting one in an assertion message.
    private static bool Same(AgentKeyRecord a, AgentKeyRecord b) =>
        a != null && b != null && string.Equals(a.agentKey, b.agentKey, StringComparison.Ordinal);

    [Test]
    public void StoreUsesTheTempRootNeverTheProjectLibrary()
    {
        Assert.That(AgentKeyStore.FilePath.StartsWith(_root, StringComparison.Ordinal), Is.True);
        Assert.That(AgentKeyStore.PendingFilePath.StartsWith(_root, StringComparison.Ordinal), Is.True);
    }

    [Test]
    public void LegacySingleKeyFileLoadsAsOneEntryAndIsMigratedOnTheNextWrite()
    {
        Directory.CreateDirectory(KeyDir);
        // Exactly what the pre-Oct-9 SDK wrote: the file IS one record.
        string legacy = "{\n    \"agentKey\": \"agk_test_legacy\",\n    \"keyId\": \"key_legacy\",\n    \"uid\": \"uid_legacy\",\n"
            + "    \"email\": \"\",\n    \"label\": \"Claude Code\",\n    \"expiresAt\": " + FutureMs + ",\n"
            + "    \"scopes\": [\"content\"],\n    \"contentId\": \"LaserTag\",\n    \"approvedByUid\": \"owner\",\n    \"approvedByEmail\": \"a***@x\"\n}";
        File.WriteAllText(AgentKeyStore.FilePath, legacy);

        var all = AgentKeyStore.LoadAll();
        Assert.That(all.Count, Is.EqualTo(1));
        Assert.That(all[0].agentKey == "agk_test_legacy", Is.True);
        Assert.That(all[0].Covers("LaserTag"), Is.True);
        Assert.That(all[0].Covers("LaserTagBeta"), Is.True);
        Assert.That(all[0].CoveredContentIds, Is.EqualTo(new[] { "LaserTag" }));
        Assert.That(all[0].storedAt, Is.GreaterThan(0), "legacy entries get the file's time as storedAt");
        Assert.That(Same(AgentKeyStore.ActiveRecord, all[0]), Is.True);

        AgentKeyStore.Save(Key("bb", "BirthdayBash"));

        string json = File.ReadAllText(AgentKeyStore.FilePath);
        StringAssert.Contains("\"schemaVersion\": 2", json);
        StringAssert.Contains("\"keys\"", json);
        var file = UnityEngine.JsonUtility.FromJson<AgentKeyFile>(json);
        Assert.That(file.keys.Count, Is.EqualTo(2), "the legacy key survives the first multi-key write");
        Assert.That(AgentKeyStore.FindFor("LaserTag").agentKey == "agk_test_legacy", Is.True);
        Assert.That(AgentKeyStore.FindFor("BirthdayBash").agentKey == "agk_test_bb", Is.True);
    }

    [Test]
    public void TwoApprovalsForTwoTitlesAreBothKept()
    {
        var laserTag = Key("lt", "LaserTag");
        var birthday = Key("bb", "BirthdayBash");
        AgentKeyStore.Save(laserTag);
        var replaced = AgentKeyStore.Save(birthday);

        Assert.That(replaced.Count, Is.EqualTo(0), "approving B must not replace A");
        Assert.That(birthday.replacedKeys.Length, Is.EqualTo(0));

        // Fresh read from disk, not the cache.
        _rootOverride.Dispose();
        _rootOverride = AgentKeyStore.UseProjectRootForTests(_root);
        var all = AgentKeyStore.LoadAll();
        Assert.That(all.Count, Is.EqualTo(2));
        Assert.That(all[0].agentKey == "agk_test_bb", Is.True, "newest first");
        Assert.That(all[1].agentKey == "agk_test_lt", Is.True);
        Assert.That(AgentKeyStore.CoveredContentIds(), Is.EquivalentTo(new[] { "LaserTag", "BirthdayBash" }));
        Assert.That(Directory.GetFiles(KeyDir, "*.tmp-*").Length, Is.EqualTo(0), "atomic write leaves no temp file");
    }

    [Test]
    public void EachTitlePicksItsOwnKeyIncludingItsBetaTwin()
    {
        AgentKeyStore.Save(Key("lt", "LaserTag"));
        AgentKeyStore.Save(Key("bb", "BirthdayBash"));
        AgentKeyStore.Save(Key("zz", "Zombiez"));

        Assert.That(AgentKeyStore.SelectFor("LaserTag").agentKey == "agk_test_lt", Is.True);
        Assert.That(AgentKeyStore.SelectFor("LaserTagBeta").agentKey == "agk_test_lt", Is.True);
        Assert.That(AgentKeyStore.SelectFor("BirthdayBash").agentKey == "agk_test_bb", Is.True);
        Assert.That(AgentKeyStore.SelectFor("BirthdayBashBeta").agentKey == "agk_test_bb", Is.True);
        Assert.That(AgentKeyStore.SelectFor("Zombiez").agentKey == "agk_test_zz", Is.True);
        Assert.That(AgentKeyStore.FindFor("LaserTagBetaBeta"), Is.Null);
        Assert.That(AgentKeyStore.FindFor("Laser"), Is.Null, "no prefix matching");

        // The header a content-scoped request sends.
        Assert.That(AuthAPI.GetUserAuthFor("LaserTagBeta") == "Bearer agk_test_lt", Is.True);
        Assert.That(AuthAPI.GetUserAuthFor("BirthdayBash") == "Bearer agk_test_bb", Is.True);

        // A content scope (what preflight/publish/inspect open) selects for every plain
        // GetUserAuth inside it, even though Zombiez is the newest key.
        using (AuthAPI.WithAgentContent("LaserTag"))
        {
            Assert.That(AgentKeyStore.ActiveRecord.agentKey == "agk_test_lt", Is.True);
            Assert.That(AuthAPI.GetUserAuth() == "Bearer agk_test_lt", Is.True);
            Assert.That(AuthAPI.userId, Is.EqualTo("uid_lt"));
            // An explicit content ID beats the scope.
            Assert.That(AuthAPI.GetUserAuthFor("BirthdayBashBeta") == "Bearer agk_test_bb", Is.True);
        }
        Assert.That(AuthAPI.GetUserAuth() == "Bearer agk_test_zz", Is.True, "outside any scope: the newest key");
    }

    [Test]
    public void ServerListedContentIdsAreCoveredToo()
    {
        var multi = Key("multi", "LaserTag");
        multi.contentIds = new[] { "LayoutLab", " LaserTag ", "" };
        AgentKeyStore.Save(multi);
        Assert.That(AgentKeyStore.LoadAll()[0].CoveredContentIds, Is.EqualTo(new[] { "LaserTag", "LayoutLab" }));
        Assert.That(AgentKeyStore.FindFor("LayoutLabBeta").agentKey == "agk_test_multi", Is.True);
    }

    [Test]
    public void FallsBackToTheNewestUsableKeyWhenNoneMatches()
    {
        AgentKeyStore.Save(Key("lt", "LaserTag"));
        AgentKeyStore.Save(Key("bb", "BirthdayBash"));

        Assert.That(AgentKeyStore.FindFor("Zombiez"), Is.Null);
        Assert.That(AgentKeyStore.SelectFor("Zombiez").agentKey == "agk_test_bb", Is.True);
        Assert.That(AuthAPI.GetUserAuthFor("Zombiez") == "Bearer agk_test_bb", Is.True);
        using (AuthAPI.WithAgentContent("Zombiez"))
            Assert.That(AgentKeyStore.ActiveRecord.agentKey == "agk_test_bb", Is.True);

        // An expired key is never selected, for its title or as the fallback.
        AgentKeyStore.Save(Key("old", "Zombiez", PastMs));
        Assert.That(AgentKeyStore.FindFor("Zombiez"), Is.Null);
        Assert.That(AgentKeyStore.SelectFor("Zombiez").agentKey == "agk_test_bb", Is.True);
        Assert.That(AgentKeyStore.Newest.agentKey == "agk_test_bb", Is.True);
        Assert.That(AgentKeyStore.LoadAll().Count, Is.EqualTo(3), "expired entries stay listed for status");
    }

    [Test]
    public void ReapprovingATitleReplacesOnlyThatTitlesEntry()
    {
        AgentKeyStore.Save(Key("lt1", "LaserTag"));
        AgentKeyStore.Save(Key("bb", "BirthdayBash"));
        var fresh = Key("lt2", "LaserTag");
        var replaced = AgentKeyStore.Save(fresh);

        Assert.That(replaced.Count, Is.EqualTo(1));
        Assert.That(replaced[0].agentKey == "agk_test_lt1", Is.True);
        Assert.That(fresh.replacedKeys, Is.EqualTo(new[] { AgentKeyStore.Mask("agk_test_lt1") }));
        var all = AgentKeyStore.LoadAll();
        Assert.That(all.Count, Is.EqualTo(2));
        Assert.That(AgentKeyStore.FindFor("LaserTag").agentKey == "agk_test_lt2", Is.True);
        Assert.That(AgentKeyStore.FindFor("BirthdayBash").agentKey == "agk_test_bb", Is.True);

        // An older key that also covered another title keeps that title.
        var multi = Key("multi", "Zombiez");
        multi.contentIds = new[] { "LayoutLab" };
        AgentKeyStore.Save(multi);
        AgentKeyStore.Save(Key("zz", "Zombiez"));
        Assert.That(AgentKeyStore.FindFor("Zombiez").agentKey == "agk_test_zz", Is.True);
        Assert.That(AgentKeyStore.FindFor("LayoutLab").agentKey == "agk_test_multi", Is.True);
        Assert.That(AgentKeyStore.FindFor("LayoutLab").CoveredContentIds, Is.EqualTo(new[] { "LayoutLab" }));
    }

    [Test]
    public void SigningOutOfOneTitleKeepsTheOthers()
    {
        AgentKeyStore.Save(Key("lt", "LaserTag"));
        AgentKeyStore.Save(Key("bb", "BirthdayBash"));

        var removed = AgentKeyStore.RemoveFor("LaserTagBeta"); // the beta twin names the same entry
        Assert.That(removed.Count, Is.EqualTo(1));
        Assert.That(removed[0].agentKey == "agk_test_lt", Is.True);
        Assert.That(AgentKeyStore.FindFor("LaserTag"), Is.Null);
        Assert.That(AgentKeyStore.FindFor("BirthdayBash").agentKey == "agk_test_bb", Is.True);
        Assert.That(File.Exists(AgentKeyStore.FilePath), Is.True);

        Assert.That(AgentKeyStore.RemoveFor("Zombiez").Count, Is.EqualTo(0));
        Assert.That(AgentKeyStore.RemoveKey("agk_test_bb"), Is.True);
        Assert.That(AgentKeyStore.LoadAll().Count, Is.EqualTo(0));
        Assert.That(File.Exists(AgentKeyStore.FilePath), Is.False, "the last entry out removes the file");
        Assert.That(AgentKeyStore.IsActive, Is.False);
    }

    [Test]
    public void SeveralTitlesCanBePendingAtOnce()
    {
        AgentKeyStore.SavePending(new PendingAgentRequest { requestId = "r1", pollToken = "akp_1", contentId = "LaserTag", expiresAt = FutureMs });
        AgentKeyStore.SavePending(new PendingAgentRequest { requestId = "r2", pollToken = "akp_2", contentId = "BirthdayBash", expiresAt = FutureMs });
        AgentKeyStore.SavePending(new PendingAgentRequest { requestId = "r3", pollToken = "akp_3", contentId = "Zombiez", expiresAt = FutureMs });

        var all = AgentKeyStore.LoadAllPending();
        Assert.That(all.Count, Is.EqualTo(3));
        Assert.That(all[0].requestId, Is.EqualTo("r3"), "newest first");
        Assert.That(AgentKeyStore.LoadPending("BirthdayBash").requestId, Is.EqualTo("r2"));

        // Asking again for the same title replaces only that title's request.
        AgentKeyStore.SavePending(new PendingAgentRequest { requestId = "r4", pollToken = "akp_4", contentId = "LaserTag", expiresAt = FutureMs });
        Assert.That(AgentKeyStore.LoadAllPending().Count, Is.EqualTo(3));
        Assert.That(AgentKeyStore.LoadPending("LaserTag").requestId, Is.EqualTo("r4"));
        Assert.That(AgentKeyStore.FindPendingByRequestId("r1"), Is.Null);

        // Resolving one leaves the others.
        Assert.That(AgentKeyStore.DeletePendingRequest("r2"), Is.True);
        Assert.That(AgentKeyStore.LoadAllPending().Count, Is.EqualTo(2));
        Assert.That(AgentKeyStore.DeletePending("Zombiez"), Is.True);
        Assert.That(AgentKeyStore.LoadAllPending().Count, Is.EqualTo(1));
        Assert.That(AgentKeyStore.LoadPending().requestId, Is.EqualTo("r4"));
        Assert.That(Directory.GetFiles(KeyDir, "*.tmp-*").Length, Is.EqualTo(0));
    }

    [Test]
    public void LegacySinglePendingFileIsReadAndMigrated()
    {
        Directory.CreateDirectory(KeyDir);
        File.WriteAllText(AgentKeyStore.PendingFilePath,
            "{\"requestId\":\"rOld\",\"pollToken\":\"akp_old\",\"contentId\":\"LaserTag\",\"sentTo\":\"a***@x\",\"expiresAt\":" + FutureMs + ",\"approval\":\"email_link\"}");

        Assert.That(AgentKeyStore.LoadAllPending().Count, Is.EqualTo(1));
        Assert.That(AgentKeyStore.LoadPending("LaserTag").requestId, Is.EqualTo("rOld"));

        AgentKeyStore.SavePending(new PendingAgentRequest { requestId = "rNew", pollToken = "akp_new", contentId = "BirthdayBash", expiresAt = FutureMs });
        var all = AgentKeyStore.LoadAllPending();
        Assert.That(all.Count, Is.EqualTo(2), "the legacy request survives the migration");
        StringAssert.Contains("\"schemaVersion\": 2", File.ReadAllText(AgentKeyStore.PendingFilePath));
    }

    [Test]
    public void ContentScopesAreRemovedByIdentityNotAsAStack()
    {
        AgentKeyStore.Save(Key("lt", "LaserTag"));
        AgentKeyStore.Save(Key("bb", "BirthdayBash"));

        var publish = AuthAPI.WithAgentContent("LaserTag");   // a long background publish
        var inspect = AuthAPI.WithAgentContent("BirthdayBash"); // a short command meanwhile
        Assert.That(AgentKeyStore.CurrentContentScope, Is.EqualTo("BirthdayBash"));
        publish.Dispose();                                       // the publish ends first
        Assert.That(AgentKeyStore.CurrentContentScope, Is.EqualTo("BirthdayBash"));
        inspect.Dispose();
        Assert.That(AgentKeyStore.CurrentContentScope, Is.Null, "a LIFO restore would have left LaserTag here");

        using (AuthAPI.WithAgentContent(null)) Assert.That(AgentKeyStore.CurrentContentScope, Is.Null);
    }

    [Test]
    public void MaskNeverRevealsTheKey()
    {
        string masked = AgentKeyStore.Mask("agk_0123456789abcdefWXYZ");
        Assert.That(masked, Is.EqualTo("agk_…WXYZ"));
        Assert.That(masked.Contains("0123456789"), Is.False);
    }
}
