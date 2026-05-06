using UnityEngine;
using Meta.XR;

public class PassthroughCameraRouter : MonoBehaviour
{
    public Renderer targetRenderer;
    public string texturePropertyName = "_MainTex";
    public PassthroughCameraAccess cameraAccess;

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
}
