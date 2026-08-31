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

        foreach (var AS in allAS) {
            AS.spatialBlend = 1f;

            var animCurve = new AnimationCurve(
                new Keyframe(AS.minDistance,1f),
                new Keyframe(AS.minDistance + (AS.maxDistance - AS.minDistance ) / 4f,.35f),
                new Keyframe(AS.maxDistance,0f));

            AS.rolloffMode = AudioRolloffMode.Custom;
            animCurve.SmoothTangents(1,.025f);
            AS.SetCustomCurve(AudioSourceCurveType.CustomRolloff,animCurve);

            AS.dopplerLevel = 0f;
            AS.spread = 60f;
        
        }
    }

    void Start()
    {
        if (DestroyOnStart)
        {
            Destroy(this);
        }
    }

}
