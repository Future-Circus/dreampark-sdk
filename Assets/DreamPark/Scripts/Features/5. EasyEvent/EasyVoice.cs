using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EasyVoice : EasyEvent
{
    public static Dictionary<GameObject, EasyVoice> easyVoices = new();
    public bool multipleClips = false;
    [ShowIf("multipleClips", false)] public AudioClip voiceLine;
    [ShowIf("multipleClips", true)] public List<AudioClip> voiceLines = new();
    [ShowIf("multipleClips", true)] public bool randomizeClips = true;
    [ShowIf("animateJaw")] public Transform upperJaw;
    [ShowIf("animateJaw")] public Transform lowerJaw;
    [ShowIf("animateJaw")] public float jawOpenAmount = 100f;
    [ShowIf("animateUpperBody")] public Transform rootBone;
    [ShowIf("animateUpperBody")] public Transform head;
    [ShowIf("animateUpperBody")] public Transform neck;
    [ShowIf("animateUpperBody")] public Transform shoulders;
    public bool animateJaw = true;
    public bool animateUpperBody = false;
    public Transform voiceOrigin;
    private Vector3 upperJawStartRot;
    private Vector3 lowerJawStartRot;
    private AudioSource audioSource;
    public bool delayNextEvent = false;

    private float shoulderWeight = 0.2f;
    private float neckWeight = 0.5f;
    private float headWeight = 0.8f;
    private float eyeWeight = 1.0f;

    private Vector2 shoulderRotationLimits = new Vector2(-10f, 10f);
    private Vector2 neckRotationLimits = new Vector2(-30f, 30f);
    private Vector2 headRotationLimits = new Vector2(-45f, 45f);
    private Vector2 eyeRotationLimits = new Vector2(-15f, 15f);
    private Transform lookTarget;
    private string ogHeadName = "";
    private string ogNeckName = "";
    private string ogShouldersName = "";
    private int clipIndex = 0;

    public override void Awake()
    {
        lookTarget = Camera.main.transform;
        if (animateUpperBody)
        {
            if (head != null)
            {
                ogHeadName = head.name ?? "";
                head.name = ogHeadName + " (EasyVoice)";
            }
            if (neck != null)
            {
                ogNeckName = neck.name ?? "";
                neck.name = ogNeckName + " (EasyVoice)";
            }
            if (shoulders != null)
            {
                ogShouldersName = shoulders.name ?? "";
                shoulders.name = ogShouldersName + " (EasyVoice)";
            }
        }
        base.Awake();
    }

    public override void Start()
    {
        if (upperJaw != null)
        {
            upperJawStartRot = upperJaw.localRotation.eulerAngles;
        }
        if (lowerJaw != null)
        {
            lowerJawStartRot = lowerJaw.localRotation.eulerAngles;
        }
        if (voiceOrigin == null)
        {
            voiceOrigin = transform;
        }
        base.Start();
    }

    public override void OnEvent(object arg0 = null)
    {
        if (easyVoices.ContainsKey(gameObject) && easyVoices[gameObject] != this)
        {
            easyVoices[gameObject].OnReplace();
        }
        easyVoices[gameObject] = this;
        isEnabled = true;

        if (audioSource != null)
        {
            audioSource.Stop();
            if (audioSource.gameObject != null)
            {
                Destroy(audioSource.gameObject);
            }
            audioSource = null;
        }

        if (multipleClips)
        {
            if (randomizeClips)
            {
                voiceLine = voiceLines[Random.Range(0, voiceLines.Count)];
            }
            else
            {
                voiceLine = voiceLines[clipIndex];
                clipIndex = (clipIndex + 1) % voiceLines.Count;
            }
        }

        if (voiceLine != null)
        {
            audioSource = voiceLine.PlaySFX(voiceOrigin.position, 1f, 1f, voiceOrigin);
            audioSource.loop = false;
        }

        if (!delayNextEvent)
        {
            onEvent?.Invoke(null);
        }
        else
        {
            StartCoroutine(DelayNextEvent(audioSource));
        }
    }
    private IEnumerator DelayNextEvent(AudioSource audioSource)
    {
        if (delayNextEvent)
        {
            yield return new WaitForSeconds(audioSource.clip.length);
        }
        else
        {
            yield return new WaitUntil(() => !audioSource.isPlaying);
        }
        onEvent?.Invoke(null);
    }
    public void OnReplace()
    {
        OnEventDisable();
        if (audioSource != null)
        {
            audioSource.Stop();
            if (audioSource.gameObject != null)
            {
                Destroy(audioSource.gameObject);
            }
            audioSource = null;
        }
    }
    public void Update()
    {
        if (!isEnabled && audioSource != null)
        {
            audioSource.Stop();
            if (audioSource.gameObject != null)
            {
                Destroy(audioSource.gameObject);
            }
            audioSource = null;
        }
        if (animateJaw && audioSource && audioSource.isPlaying)
        {
            AnimateJaw(audioSource);
            DetectFrequencyPulse(audioSource);
        }
        if (animateUpperBody && audioSource && audioSource.isPlaying)
        {
            RotateTowardsTarget(shoulders, shoulderWeight, shoulderRotationLimits);
            RotateTowardsTarget(neck, neckWeight, neckRotationLimits);
            RotateTowardsTarget(head, headWeight, headRotationLimits);
        }
    }
    void AnimateJaw(AudioSource audioSource)
    {
        float[] audioSamples = new float[256];
        audioSource.GetOutputData(audioSamples, 0);

        float currentVolume = 0f;
        foreach (float sample in audioSamples)
        {
            currentVolume += Mathf.Abs(sample);
        }
        currentVolume /= audioSamples.Length;

        float jawRotation = Mathf.Lerp(0f, jawOpenAmount, currentVolume);
        if (upperJaw != null)
        {
            upperJaw.localRotation = Quaternion.Euler(upperJawStartRot.x + jawRotation * 0.5f, upperJawStartRot.y, upperJawStartRot.z);
        }
        if (lowerJaw != null)
        {
            lowerJaw.localRotation = Quaternion.Euler(lowerJawStartRot.x - jawRotation * 1.5f, lowerJawStartRot.y, lowerJawStartRot.z);
        }
    }

    void DetectFrequencyPulse(AudioSource audioSource)
    {
        float[] spectrum = new float[1024];
        audioSource.GetSpectrumData(spectrum, 0, FFTWindow.Rectangular);
        float sampleRate = AudioSettings.outputSampleRate;
        float freqPerBin = sampleRate / 2f / 1024;
        int targetBin = Mathf.FloorToInt(30f / freqPerBin);
        if (targetBin >= 0 && targetBin < spectrum.Length && spectrum[targetBin] >= 0.1f)
        {

        }
    }

    void RotateTowardsTarget(Transform bone, float weight, Vector2 rotationLimits, bool onlyY = false)
    {
        // Calculate direction to target in root bone's local space
        Vector3 directionToTarget = rootBone.InverseTransformDirection(lookTarget.position - rootBone.position);
        Quaternion targetRotation = Quaternion.LookRotation(directionToTarget, rootBone.up);

        if (onlyY)
        {
            targetRotation = Quaternion.Euler(0f, targetRotation.eulerAngles.y, 0f);
        }

        // Slerp towards the target rotation
        Quaternion currentRotation = Quaternion.Slerp(rootBone.localRotation, targetRotation, weight);

        Quaternion localRotation = Quaternion.Slerp(bone.localRotation, currentRotation, 5f * Time.deltaTime);

        // Convert to Euler to apply limits
        Vector3 currentEulerAngles = localRotation.eulerAngles;
        currentEulerAngles = NormalizeAngles(currentEulerAngles);

        currentEulerAngles.x = Mathf.Clamp(currentEulerAngles.x, rotationLimits.x, rotationLimits.y);
        currentEulerAngles.y = Mathf.Clamp(currentEulerAngles.y, rotationLimits.x, rotationLimits.y);

        // Apply the clamped rotation in local space
        bone.localRotation = Quaternion.Euler(currentEulerAngles);
    }

    Vector3 NormalizeAngles(Vector3 angles)
    {
        angles.x = (angles.x > 180) ? angles.x - 360 : angles.x;
        angles.y = (angles.y > 180) ? angles.y - 360 : angles.y;
        angles.z = (angles.z > 180) ? angles.z - 360 : angles.z;
        return angles;
    }
}
