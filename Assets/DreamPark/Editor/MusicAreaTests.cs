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
}
