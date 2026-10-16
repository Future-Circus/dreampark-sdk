#if UNITY_EDITOR && !DREAMPARKCORE
using System.Reflection;
using DreamPark;
using DreamPark.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class DreamSequenceButtonFeedbackTests
{
    [Test]
    public void DefaultsKeepTriggersFixedAndAnimateOnlyTheVisual()
    {
        GameObject start = AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceControlPrefabBuilder.StartButtonPrefabPath);
        GameObject elevator = AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceControlPrefabBuilder.ElevatorControlsPrefabPath);
        Assert.That(start, Is.Not.Null);
        Assert.That(elevator, Is.Not.Null);
        Assert.That(start.GetComponentsInChildren<DreamSequenceButtonFeedback>(true).Length,
            Is.EqualTo(1));
        Assert.That(elevator.GetComponentsInChildren<DreamSequenceButtonFeedback>(true).Length,
            Is.EqualTo(2));

        foreach (GameObject prefab in new[] { start, elevator })
            foreach (DreamSequenceButtonFeedback button in
                prefab.GetComponentsInChildren<DreamSequenceButtonFeedback>(true))
            {
                Assert.That(button.GetComponent<Collider>(), Is.Not.Null);
                Assert.That(button.GetComponent<MeshRenderer>(), Is.Null);
                Assert.That(button.visual, Is.Not.Null);
                Assert.That(button.visual.GetComponent<MeshRenderer>(), Is.Not.Null);
                Assert.That(button.visual.GetComponent<Collider>(), Is.Null);
                Assert.That(button.GetComponent<LuaBehaviour>(), Is.Not.Null);
                Assert.That(button.resetAfterSeconds, Is.EqualTo(2f));
            }
    }

    [Test]
    public void PressSquishesThenResetsTwoSecondsAfterActivation()
    {
        const float flat = 0.08f;
        Assert.That(DreamSequenceButtonFeedback.EvaluateHeight(0f, 0.12f, 2f, 0.18f, flat),
            Is.EqualTo(1f).Within(0.001f));
        Assert.That(DreamSequenceButtonFeedback.EvaluateHeight(0.12f, 0.12f, 2f, 0.18f, flat),
            Is.EqualTo(flat).Within(0.001f));
        Assert.That(DreamSequenceButtonFeedback.EvaluateHeight(1.5f, 0.12f, 2f, 0.18f, flat),
            Is.EqualTo(flat).Within(0.001f));
        Assert.That(DreamSequenceButtonFeedback.EvaluateHeight(2.18f, 0.12f, 2f, 0.18f, flat),
            Is.EqualTo(1f).Within(0.001f));
    }

    [Test]
    public void DefaultPressHasAnAudibleSoundWithoutExternalAssets()
    {
        MethodInfo generate = typeof(DreamSequenceButtonFeedback).GetMethod(
            "GetDefaultPressSound", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(generate, Is.Not.Null);
        AudioClip clip = generate.Invoke(null, null) as AudioClip;
        Assert.That(clip, Is.Not.Null);
        Assert.That(clip.length, Is.GreaterThan(0.1f));
        var samples = new float[clip.samples];
        Assert.That(clip.GetData(samples, 0), Is.True);
        Assert.That(System.Array.Exists(samples, sample => Mathf.Abs(sample) > 0.01f), Is.True);
    }
}
#endif
