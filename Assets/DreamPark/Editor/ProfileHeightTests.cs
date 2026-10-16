using System.Reflection;
using Defective.JSON;
using DreamPark.API;
using NUnit.Framework;

public sealed class ProfileHeightTests
{
    private static readonly MethodInfo Hydrate = typeof(ProfileAPI).GetMethod(
        "HydrateProfileSnapshot",
        BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo ApplySnapshot = typeof(ProfileAPI).GetMethod(
        "ApplyProfileSnapshot",
        BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo SubscribeLua = typeof(ProfileAPI).GetMethod(
        "SubscribeLuaHeightChanged",
        BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo UnsubscribeLua = typeof(ProfileAPI).GetMethod(
        "UnsubscribeLuaHeightChanged",
        BindingFlags.Static | BindingFlags.NonPublic);

    [SetUp]
    public void SetUp()
    {
        ProfileAPI.ClearIdentity();
    }

    [TearDown]
    public void TearDown()
    {
        ProfileAPI.ClearIdentity();
    }

    [TestCase(24f)]
    [TestCase(36f)]
    [TestCase(68f)]
    [TestCase(95f)]
    [TestCase(96f)]
    public void HydrateProfileSnapshot_UsesCanonicalHeightInches(float inches)
    {
        ProfileAPI.ProfileSnapshot snapshot = Snapshot("{\"heightInches\":" +
            inches.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

        Assert.That(snapshot.heightInches, Is.EqualTo(inches).Within(0.0001f));
        Assert.That(snapshot.heightFactor,
            Is.EqualTo(inches / ProfileAPI.ReferenceHeightInches).Within(0.0001f));
    }

    [TestCase("{}")]
    [TestCase("{\"heightInches\":null}")]
    [TestCase("{\"heightInches\":\"68\"}")]
    [TestCase("{\"heightInches\":23.9}")]
    [TestCase("{\"heightInches\":95.5}")]
    [TestCase("{\"heightInches\":96.1}")]
    public void HydrateProfileSnapshot_DefaultsMissingOrMalformedHeight(string profileJson)
    {
        ProfileAPI.ProfileSnapshot snapshot = Snapshot(profileJson);

        Assert.That(snapshot.heightInches, Is.EqualTo(ProfileAPI.ReferenceHeightInches));
        Assert.That(snapshot.heightFactor, Is.EqualTo(1f));
    }

    [Test]
    public void UnitConversions_UseFiveFootEightReference()
    {
        Assert.That(ProfileAPI.ReferenceHeightInches, Is.EqualTo(68f));
        Assert.That(ProfileAPI.ReferenceHeightInches * ProfileAPI.InchesToMeters,
            Is.EqualTo(1.7272f).Within(0.0001f));
    }

    [Test]
    public void HeightChanged_IsSilentForInitialHydration_ThenFiresOnlyForChanges()
    {
        int calls = 0;
        float receivedInches = 0;
        float receivedFactor = 0;
        System.Action<float, float> handler = (inches, factor) =>
        {
            calls++;
            receivedInches = inches;
            receivedFactor = factor;
        };
        ProfileAPI.OnHeightChanged += handler;

        try
        {
            ProfileAPI.BindIdentity("height-test", null, null, RootWithHeight(36f));
            Assert.That(ProfileAPI.IsLoaded, Is.True);
            Assert.That(ProfileAPI.HeightInches, Is.EqualTo(36f));
            Assert.That(calls, Is.Zero, "initial hydration must only establish the baseline");

            Assert.That(ProfileAPI.ApplyHeightUpdate(36f), Is.False);
            Assert.That(calls, Is.Zero, "same-value updates must not emit");

            Assert.That(ProfileAPI.ApplyHeightUpdate(48f), Is.True);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(receivedInches, Is.EqualTo(48f));
            Assert.That(receivedFactor, Is.EqualTo(48f / ProfileAPI.ReferenceHeightInches).Within(0.0001f));

            Assert.That(ProfileAPI.ApplyHeightUpdate(48f), Is.False);
            Assert.That(calls, Is.EqualTo(1));

            Assert.That(ProfileAPI.ApplyHeightUpdate(float.NaN), Is.True);
            Assert.That(ProfileAPI.HeightInches, Is.EqualTo(ProfileAPI.ReferenceHeightInches));
            Assert.That(calls, Is.EqualTo(2), "malformed runtime input changes to the safe fallback once");
        }
        finally
        {
            ProfileAPI.OnHeightChanged -= handler;
        }
    }

    [Test]
    public void LaterSnapshotRefresh_UsesHeightChangePath()
    {
        ProfileAPI.BindIdentity("height-test", null, null, RootWithHeight(36f));
        int calls = 0;
        System.Action<float, float> handler = (inches, factor) => calls++;
        ProfileAPI.OnHeightChanged += handler;

        try
        {
            Assert.That(ApplySnapshot, Is.Not.Null);
            ApplySnapshot.Invoke(null, new object[] { Snapshot("{\"heightInches\":42}"), null });
            Assert.That(ProfileAPI.HeightInches, Is.EqualTo(42f));
            Assert.That(calls, Is.EqualTo(1));

            ApplySnapshot.Invoke(null, new object[] { Snapshot("{\"heightInches\":42}"), null });
            Assert.That(calls, Is.EqualTo(1));
        }
        finally
        {
            ProfileAPI.OnHeightChanged -= handler;
        }
    }

    [Test]
    public void LuaHeightSubscription_UnsubscribeIsSafeAndIdentityScoped()
    {
        ProfileAPI.BindIdentity("height-test", null, null, RootWithHeight(36f));
        Assert.That(SubscribeLua, Is.Not.Null);
        Assert.That(UnsubscribeLua, Is.Not.Null);

        int calls = 0;
        var callback = new System.Action<float, float>((inches, factor) => calls++);
        int subscriptionId = (int)SubscribeLua.Invoke(null, new object[] { callback });

        ProfileAPI.ApplyHeightUpdate(40f);
        Assert.That(calls, Is.EqualTo(1));

        UnsubscribeLua.Invoke(null, new object[] { subscriptionId });
        UnsubscribeLua.Invoke(null, new object[] { subscriptionId });
        ProfileAPI.ApplyHeightUpdate(41f);
        Assert.That(calls, Is.EqualTo(1), "unsubscribe must be idempotent");

        subscriptionId = (int)SubscribeLua.Invoke(null, new object[] { callback });
        ProfileAPI.ClearIdentity();
        ProfileAPI.BindIdentity("next-height-test", null, null, RootWithHeight(50f));
        ProfileAPI.ApplyHeightUpdate(51f);
        Assert.That(calls, Is.EqualTo(1), "callbacks must not cross an identity clear");
        UnsubscribeLua.Invoke(null, new object[] { subscriptionId });
    }

    private static ProfileAPI.ProfileSnapshot Snapshot(string profileJson)
    {
        Assert.That(Hydrate, Is.Not.Null);
        var root = JSONObject.Create("{\"identity\":{\"kind\":\"user\",\"id\":\"test\"},\"profile\":" + profileJson + "}");
        return (ProfileAPI.ProfileSnapshot)Hydrate.Invoke(null, new object[] { root });
    }

    private static JSONObject RootWithHeight(float inches)
    {
        return JSONObject.Create("{\"identity\":{\"kind\":\"user\",\"id\":\"test\"},\"profile\":{\"heightInches\":" +
            inches.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}");
    }
}
