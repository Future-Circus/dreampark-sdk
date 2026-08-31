using System.Collections;
using UnityEngine;
using System.Collections.Generic;

public class EasyAudio : EasyEvent
{
    public static Dictionary<GameObject, EasyAudio> easyAudios = new();
    [ShowIf("multipleClips", false)] public AudioClip audioClip;
    public bool multipleClips = false;
    [ShowIf("multipleClips", true)] public List<AudioClip> audioClips = new();
    [ShowIf("multipleClips", true)] public bool randomizeClips = true;
    public float volume = 1f;
    public float pitch = 1f;
    public float pitchVariation = 0.1f;
    public bool loop = false;
    public bool delayNextEvent = false;
    public bool replaceLastAudio = false;
    private AudioSource audioSource;
    private int clipIndex = 0;
    private Coroutine delayedEventCoroutine;
    private bool waitForPlaybackDisable = false;

    public override void Start()
    {
        base.Start();
    }

    public override void OnEvent(object arg0 = null)
    {
        if (replaceLastAudio && easyAudios.ContainsKey(gameObject) && easyAudios[gameObject] != this)
        {
            easyAudios[gameObject].OnReplace();
        }
        easyAudios[gameObject] = this;
        isEnabled = true;
        waitForPlaybackDisable = false;

        if (delayedEventCoroutine != null)
        {
            StopCoroutine(delayedEventCoroutine);
            delayedEventCoroutine = null;
        }

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
                audioClip = audioClips[Random.Range(0, audioClips.Count)];
            }
            else
            {
                audioClip = audioClips[clipIndex];
                clipIndex = (clipIndex + 1) % audioClips.Count;
            }
        }

        if (audioClip != null)
        {
            Debug.Log("[EasyAudio] OnEvent: " + audioClip.name + " replacing last audio");
            audioSource = audioClip.PlaySFX(transform.position, volume, pitch + Random.Range(-pitchVariation, pitchVariation), loop ? transform : null);
            if (!loop)
            {
                // Keep state green while one-shot audio is still playing.
                waitForPlaybackDisable = true;
            }
        }
        else if (!loop)
        {
            OnEventDisable();
        }

        if (!delayNextEvent)
        {
            onEvent?.Invoke(null);
        }
        else
        {
            delayedEventCoroutine = StartCoroutine(DelayNextEvent(audioSource));
        }
    }

    public void Update()
    {
        if (isEnabled && waitForPlaybackDisable && (audioSource == null || !audioSource.isPlaying))
        {
            waitForPlaybackDisable = false;
            OnEventDisable();
        }

        if (!isEnabled && audioSource != null)
        {
            audioSource.Stop();
            if (audioSource.gameObject != null)
            {
                Destroy(audioSource.gameObject);
            }
            audioSource = null;
        }
    }

    private IEnumerator DelayNextEvent(AudioSource audioSource)
    {
        if (audioSource != null && delayNextEvent)
        {
            yield return new WaitForSeconds(audioSource.clip.length);
        }
        else if (audioSource != null)
        {
            yield return new WaitUntil(() => !audioSource.isPlaying);
        }
        onEvent?.Invoke(null);
        delayedEventCoroutine = null;
    }

    public void OnReplace()
    {
        if (delayedEventCoroutine != null)
        {
            StopCoroutine(delayedEventCoroutine);
            delayedEventCoroutine = null;
        }
        waitForPlaybackDisable = false;
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
}
