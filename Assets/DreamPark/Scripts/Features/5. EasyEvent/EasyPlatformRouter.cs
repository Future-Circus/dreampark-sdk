using UnityEngine;
using UnityEngine.XR;

public class EasyPlatformRouter : EasyEvent
{
    [Header("Platform Routes")]
    public EasyEvent headsetRoute;
    public EasyEvent mobileViewerRoute;

    [Tooltip("If true and selected route is missing, continue to next EasyEvent in chain.")]
    public bool invokeNextIfMissingRoute = true;

    [ReadOnly] public string runtimeResolvedPlatform = "Unknown";
    [ReadOnly] public string runtimeResolvedRoute = "None";

    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;

        bool isMobileViewer = IsMobileViewer();
        runtimeResolvedPlatform = isMobileViewer ? "Mobile Viewer" : "Headset";

        EasyEvent route = isMobileViewer ? mobileViewerRoute : headsetRoute;
        runtimeResolvedRoute = route != null ? route.GetType().Name : "None";

        if (route == this)
        {
            Debug.LogWarning("[EasyPlatformRouter] Route points to self on " + gameObject.name + ". Ignoring.");
            if (invokeNextIfMissingRoute)
                onEvent?.Invoke(arg0);
            return;
        }

        if (route != null)
        {
            route.OnEvent(arg0);
            return;
        }

        if (invokeNextIfMissingRoute)
            onEvent?.Invoke(arg0);
    }

    private static bool IsMobileViewer()
    {
#if UNITY_IOS || UNITY_ANDROID
        // On mobile builds, treat as headset only when XR runtime is actually active.
        return !(XRSettings.enabled && XRSettings.isDeviceActive);
#else
        return false;
#endif
    }
}
