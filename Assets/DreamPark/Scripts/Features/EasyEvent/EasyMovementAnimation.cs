using UnityEngine;
using System;
using System.Collections;

public class EasyMovementAnimation : EasyAnimation
{
    public string animatorWalkParam = "isWalking";
    public string animatorRunParam = "isRunning";
    private Vector3 lastPosition;
    public override void Awake()
    {
        base.Awake();
        animator = GetComponentInChildren<Animator>();
    }
    public override void OnEvent(object arg0 = null)
    {
        if (easyAnimations.ContainsKey(gameObject) && easyAnimations[gameObject] != this) {
            easyAnimations[gameObject].OnReplace();
        }
        easyAnimations[gameObject] = this;
        isEnabled = true;
        onEvent?.Invoke(arg0);
    }
    public void Update()
    {
        if (!isEnabled)
        {
            lastPosition = transform.position;
            return;
        }
        var velocity = (transform.position - lastPosition) / Time.deltaTime;
        Debug.Log("[EasyMovementAnimation] " + gameObject.name + " : Velocity: " + velocity.magnitude);
        if (velocity.magnitude > 0.1f) {
            animator.SetBool(animatorWalkParam, true);
            animator.SetBool(animatorRunParam, false);
            animator.speed = velocity.magnitude;
        } else if (velocity.magnitude > 1f) {
            animator.SetBool(animatorRunParam, true);
            animator.SetBool(animatorWalkParam, true);
            animator.speed = Mathf.Max(velocity.magnitude/2f, 1f);
        } else {
            animator.SetBool(animatorRunParam, false);
            animator.SetBool(animatorWalkParam, false);
            animator.speed = 1f;
        }
        lastPosition = transform.position;
    }

    public override void OnEventDisable() {
        base.OnEventDisable();
        animator.SetBool(animatorWalkParam, false);
        animator.SetBool(animatorRunParam, false);
        animator.speed = 1f;
    }
}
