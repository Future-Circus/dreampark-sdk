using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RealisticRolloff : MonoBehaviour
{
    public bool DestroyOnStart = true;
    // Start is called before the first frame update
    void Awake()
    {
        var allAS = GetComponents<AudioSource>();

        if(allAS == null || allAS.Length == 0)
        {
            Debug.LogError("RealisticRolloff script requires at least one AudioSource component");
            return;
        }

        foreach (var AS in allAS) Apply(AS);
    }

    /// <summary>
    /// The realistic rolloff on one AudioSource (spatial 1, custom curve from its min/max distance, no doppler, spread 60).
    /// Nov 11 2026 (pt-perf): the curve is built once per distance range and shared (SetCustomCurve copies it), instead
    /// of a new AnimationCurve per component; CoreExtensions.PlaySFX applies it directly with no component per sound.
    /// </summary>
    public static void Apply(AudioSource AS)
    {
        if (AS == null) return;
        AS.spatialBlend = 1f;
        AnimationCurve animCurve = CurveFor(AS.minDistance, AS.maxDistance);
        AS.rolloffMode = AudioRolloffMode.Custom;
        AS.SetCustomCurve(AudioSourceCurveType.CustomRolloff, animCurve);
        AS.dopplerLevel = 0f;
        AS.spread = 60f;
    }

    const int MaxSharedCurves = 64;
    static readonly Dictionary<(float, float), AnimationCurve> s_curves = new Dictionary<(float, float), AnimationCurve>();

    /// The rolloff curve for (min, max) distance: 1 at min, .35 a quarter of the way, 0 at max, smoothed at the middle key.
    public static AnimationCurve CurveFor(float minDistance, float maxDistance)
    {
        if (s_curves.TryGetValue((minDistance, maxDistance), out AnimationCurve shared)) return shared;
        var animCurve = new AnimationCurve(
            new Keyframe(minDistance,1f),
            new Keyframe(minDistance + (maxDistance - minDistance ) / 4f,.35f),
            new Keyframe(maxDistance,0f));
        animCurve.SmoothTangents(1,.025f);
        if (s_curves.Count < MaxSharedCurves) s_curves[(minDistance, maxDistance)] = animCurve;
        return animCurve;
    }

    void Start()
    {
        if (DestroyOnStart)
        {
            Destroy(this);
        }
    }

}
