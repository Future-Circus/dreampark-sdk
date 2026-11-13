using UnityEngine;
using Meta.XR;

public class PassthroughCameraRouter : MonoBehaviour
{
    // Quest-only (Meta passthrough camera). The iOS player is built without the Meta
    // assemblies (Assets/Editor/IosQuestAssemblyFilter.cs), so on iOS this is an empty shell.
    // SERIALIZED ON EVERY PLATFORM, on purpose: same reason as HandTracker.leftHand / rightHand. An iOS bundle's layout
    // comes from the Editor, so a serialized field behind a platform #if makes the bundle fatal to load on iOS.
    public Renderer targetRenderer;
    public string texturePropertyName = "_MainTex";
    public PassthroughCameraAccess cameraAccess;

#if !UNITY_IOS || UNITY_EDITOR
    void Start()
    {
        cameraAccess = FindFirstObjectByType<PassthroughCameraAccess>();
        if (cameraAccess == null)
        {
            enabled = false;
            Debug.LogError("PassthroughCameraRouter: No PassthroughCameraAccess found");
            return;
        }
        Texture texture = cameraAccess.GetTexture();
        if (texture == null)
        {
            enabled = false;
            Debug.LogError("PassthroughCameraRouter: No texture found");
            return;
        }
        if (targetRenderer == null)
        {
            enabled = false;
            Debug.LogError("PassthroughCameraRouter: No target renderer found");
            return;
        }
        targetRenderer.material.SetTexture(texturePropertyName, texture);
    }
#endif
}

#if UNITY_IOS && !UNITY_EDITOR
// The iOS player has no Meta assemblies (Assets/Editor/IosQuestAssemblyFilter.cs). This empty stand-in keeps the
// serialized layout the same as the Editor's. See the fields above.
namespace Meta.XR
{
    public class PassthroughCameraAccess : MonoBehaviour { }
}
#endif
