using DreamPark;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// Oct 2026: MusicArea.Awake added RealisticRolloff BEFORE the AudioSource, so its Awake logged
// "RealisticRolloff script requires at least one AudioSource component" for every music area
// (every simulated-peer run) and changed nothing. The line is gone; the music must keep the
// sound it has always shipped with (Unity's default rolloff, untouched by RealisticRolloff).
public sealed class MusicAreaTests
{
    GameObject _go;
    AudioClip _clip;

    [TearDown]
    public void TearDown()
    {
        if (_go != null) Object.DestroyImmediate(_go);
        if (_clip != null) Object.DestroyImmediate(_clip);
    }

    [Test]
    public void MusicEmitterHasNoRealisticRolloffAndKeepsTheShippedSound()
    {
        _go = new GameObject("Music Area Test") { hideFlags = HideFlags.HideAndDontSave };
        _clip = AudioClip.Create("music", 4410, 1, 44100, false);
        var area = _go.AddComponent<MusicArea>();
        area.musicTrack = _clip;
        area.Awake();   // edit mode: Unity does not call it for us

        var emitter = _go.transform.Find("MusicEmitter");
        Assert.That(emitter, Is.Not.Null);
        Assert.That(emitter.GetComponent<RealisticRolloff>(), Is.Null,
            "added before the AudioSource it only ever logged an error; it must not be added at all");
        var source = emitter.GetComponent<AudioSource>();
        Assert.That(source, Is.Not.Null);
        // What every title's music has always sounded like (RealisticRolloff never applied):
        Assert.That(source.rolloffMode, Is.EqualTo(AudioRolloffMode.Logarithmic));
        Assert.That(source.dopplerLevel, Is.EqualTo(1f));
        Assert.That(source.spread, Is.EqualTo(0f));
        Assert.That(source.spatialBlend, Is.EqualTo(1f));
        Assert.That(source.loop, Is.True);
        Assert.That(source.clip, Is.SameAs(_clip));
    }

    // ── Phone music (Oct 23 2026, phone-music): which area a phone plays (MusicAreaPhoneRules; the full table runs in
    //    Tools/phone-music-offline). A phone has no head walking into areas: full-screen AR follows its camera, the map
    //    view (diorama) its focus (tour stop / headsets), else what plays, else the park's main area.
    static MusicCandidate Cand(int id, float x, float z, float w, float d, int priority = 0, bool track = true) =>
        new MusicCandidate
        {
            id = id, center = new Vector3(x, 0f, z), right = Vector3.right, up = Vector3.up, forward = Vector3.forward,
            halfExtents = new Vector3(w / 2f, 50f, d / 2f), priority = priority, hasTrack = track,
        };

    [Test]
    public void PhoneInFullScreenArPlaysTheAreaItsCameraIsIn()
    {
        var a = new[] { Cand(0, 0, 0, 4, 4), Cand(1, 10, 0, 4, 4) };
        Assert.That(MusicAreaPhoneRules.Pick(MusicView.Ar, a, new Vector3(10, 1.5f, 1), null, -1, true), Is.EqualTo(1));
        Assert.That(MusicAreaPhoneRules.Pick(MusicView.Ar, a, new Vector3(3, 1.5f, 0), null, -1, true), Is.EqualTo(0),
            "a doorway 1 m off A hears A");
        Assert.That(MusicAreaPhoneRules.Pick(MusicView.Ar, a, new Vector3(5, 1.5f, 20), null, -1, true), Is.EqualTo(-1),
            "far from every area: none, the app's theme covers");
    }

    [Test]
    public void PhoneMapViewFollowsItsFocusElseKeepsMusic()
    {
        var a = new[] { Cand(0, 0, 0, 4, 4), Cand(1, 10, 0, 8, 8) };
        Vector3 far = new Vector3(100, 1.5f, 100);
        Assert.That(MusicAreaPhoneRules.Pick(MusicView.Diorama, a, far, new[] { new Vector3(0, 1.5f, 0) }, -1, true), Is.EqualTo(0),
            "the tour stop / headsets");
        Assert.That(MusicAreaPhoneRules.Pick(MusicView.Diorama, a, far, new Vector3[0], 0, true), Is.EqualTo(0), "keeps what plays");
        Assert.That(MusicAreaPhoneRules.Pick(MusicView.Diorama, a, far, new Vector3[0], -1, true), Is.EqualTo(1),
            "the main area (biggest)");
        Assert.That(MusicAreaPhoneRules.GainFor(MusicView.Diorama, false), Is.EqualTo(0.5f).Within(1e-4f));
        Assert.That(MusicAreaPhoneRules.GainFor(MusicView.Ar, true), Is.EqualTo(1f).Within(1e-4f),
            "the shimmer never lowers the music (it ducks under it)");
    }

    [Test]
    public void PhonePathIsOffOnAHeadset()
    {
        MusicAreaPhone.ResetForTests();
        try
        {
            MusicAreaPhone.Source = () => new MusicListen { view = MusicView.Ar, gain = 1f };
            DreamParkDevice.Set(DreamParkDeviceKind.Headset);
            Assert.That(MusicAreaPhone.Active, Is.False, "a headset keeps the head-in-area rule");
            Assert.That(MusicAreaPhone.FocusObject(), Is.Null);
            Assert.That(MusicAreaPhone.LuaGain(), Is.EqualTo(1f));
        }
        finally
        {
            DreamParkDevice.ResetToDefault();
            MusicAreaPhone.ResetForTests();
        }
    }
}
