using UnityEngine;
using System.Reflection;

/// <summary>
/// AnimationEventRouter ties to a specific EasyAnimationEvent, enabling routing of animation events
/// from an Animator GameObject to the correct target object/method. This allows AnimationEvent Event
/// methods to work across separate objects/components.
/// </summary>
public class AnimationEventRouter : MonoBehaviour
{
    public GameObject target;
    public EasyAnimationEvent linkedEvent; // Reference to the EasyAnimationEvent this router is tied to

    /// <summary>
    /// Called by AnimationEvent. Forwards the routed method call to the correct target EasyAnimationEvent.
    /// </summary>
    /// <param name="functionName">The method name to invoke (usually "OnAnimationEvent_{instanceid}")</param>
    public void RouteEvent(string functionName)
    {
        if (!target)
            return;

        // If linkedEvent exists and target is the EasyAnimationEvent's GameObject, call its route method
        if (linkedEvent != null && target == linkedEvent.gameObject)
        {
            // Calls EasyAnimationEvent.OnAnimationEvent_Route, passing through functionName
            linkedEvent.OnAnimationEvent_Route(functionName);
            return;
        }

        // Fallback: Try to dynamically invoke a parameterless method matching functionName on any MonoBehaviour on target
        var behaviours = target.GetComponents<MonoBehaviour>();
        foreach (var mb in behaviours)
        {
            if (mb == null) continue;
            var method = mb.GetType().GetMethod(
                functionName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                CallingConventions.Any,
                System.Type.EmptyTypes,
                null
            );
            if (method != null)
            {
                method.Invoke(mb, null);
                return;
            }
        }

        Debug.LogWarning($"AnimationEventRouter: Could not find method '{functionName}' on {target.name}");
    }
}
