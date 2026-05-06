using UnityEngine;

public class EasyMaterial : EasyEvent
{
    public Renderer renderer;
    public Material material;
    public Texture2D texture;
    public Texture2D normalMap;
    public override void OnEvent(object arg0 = null)
    {
        if (renderer == null) {
            renderer = GetComponent<Renderer>();
            if (renderer == null) {
                Debug.LogError("EasyMaterial: No renderer found");
                return;
            }
        }
        if (material != null) {
            renderer.material = material;
        }
        if (texture != null) {
            renderer.material.SetTexture("_baseTex", texture);
        }
        if (normalMap != null) {
            renderer.material.SetTexture("_nrmTex", normalMap);
        }
        onEvent?.Invoke(arg0);
    }
}
