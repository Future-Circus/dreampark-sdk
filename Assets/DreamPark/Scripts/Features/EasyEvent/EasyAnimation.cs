using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;

public class EasyAnimation : EasyEvent
{
    public static Dictionary<GameObject, EasyAnimation> easyAnimations = new();
    public string animationName;
    public bool animationValue = false;
    //public float animationLength = 0f;
    public bool delayNextEvent = false;
    public bool resetOnNext = false;
    [HideInInspector] public Animator animator;
    private Coroutine c;
    public override void Awake()
    {
        base.Awake();
        animator = GetComponentInChildren<Animator>();
    }
    public override void OnEvent(object arg0 = null)
    {
        if (easyAnimations.ContainsKey(gameObject) && easyAnimations[gameObject] != this)
        {
            easyAnimations[gameObject].OnReplace();
        }
        easyAnimations[gameObject] = this;

        Debug.Log(gameObject.name + " :1 " + animationName + " Start event");
        animator.SetBool(animationName, animationValue);

        if (delayNextEvent)
        {
            Debug.Log(gameObject.name + " :2 " + animationName + " Start delay next event");
            isEnabled = true;
            OnAnimationComplete(() =>
            {
                if (!isEnabled)
                {
                    return;
                }
                Debug.Log(gameObject.name + " :3 " + animationName + " Animation complete");
                onEvent?.Invoke(null);
                OnEventDisable();
                Debug.Log(gameObject.name + " :4 " + animationName + " Invoke next event");
                if (resetOnNext)
                {
                    animator.SetBool(animationName, !animationValue);
                }
            });
        }

        if (!delayNextEvent)
        {
            onEvent?.Invoke(null);
        }
    }

    public void CancelOnAnimationComplete(Coroutine c)
    {
        StopCoroutine(c);
    }

    public void OnAnimationComplete(Action onComplete, string animationName = null)
    {
        if (!animator.gameObject.activeInHierarchy || !animator.enabled)
        {
            c = null;
            return;
        }
        StartCoroutine(WaitForAnimation(onComplete, animationName));
    }

    private IEnumerator WaitForAnimation(Action onComplete, string animationName)
    {
        yield return null;
        // Wait until any animation state is no longer transitioning
        while (animator.IsInTransition(0))
        {
            if (!isEnabled)
            {
                yield break;
            }
            yield return null;
        }

        // Determine the animation name if not provided
        if (string.IsNullOrEmpty(animationName))
        {
            if (animator.GetCurrentAnimatorClipInfo(0).Length > 0)
            {
                animationName = animator.GetCurrentAnimatorClipInfo(0)[0].clip.name;
            }
        }

        if (string.IsNullOrEmpty(animationName))
        {
            if (animator.GetNextAnimatorClipInfo(0).Length > 0)
            {
                animationName = animator.GetNextAnimatorClipInfo(0)[0].clip.name;
            }
        }

        if (string.IsNullOrEmpty(animationName))
        {
            yield break;
        }

        if (animator.GetCurrentAnimatorStateInfo(0).normalizedTime > 1f)
        {
            animator.playbackTime = 0;
            yield return null;
        }

        // Get the hash of the current animation state to detect changes
        int currentAnimationHash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;

        while (animator.GetCurrentAnimatorStateInfo(0).normalizedTime < 1.0f)
        {
            if (!isEnabled)
            {
                yield break;
            }

            if (animator.IsInTransition(0))
            {
                yield return null;
            }

            // Break if a new animation starts
            if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash != currentAnimationHash)
            {
                yield return null;
            }

            yield return null;
        }

        // Trigger the callback action
        onComplete?.Invoke();

        yield return null;
    }

    public virtual void OnReplace()
    {
        OnEventDisable();
        if (!string.IsNullOrEmpty(animationName) && resetOnNext)
        {
            animator.SetBool(animationName, !animationValue);
        }
    }
}
