using UnityEngine;
using TMPro;
public class FPSDisplay : MonoBehaviour
{
    private TMP_Text fpsText;
    private float deltaTime;

    void Start()
    {
        // Create a 3D TextMeshPro object
        GameObject textObj = new GameObject("FPSText");
        textObj.transform.SetParent(Camera.main.transform);
        textObj.transform.localPosition = new Vector3(0, -0.2f, 0.5f);
        textObj.transform.localRotation = Quaternion.identity;
        textObj.transform.localScale = Vector3.one * 0.01f;

        fpsText = textObj.AddComponent<TextMeshPro>();
        fpsText.fontSize = 40;
        fpsText.color = Color.white;
        fpsText.alignment = TextAlignmentOptions.Center;
        fpsText.text = "FPS";

        // Optional: Disable extra features for performance
        fpsText.textWrappingMode = TextWrappingModes.NoWrap;
        fpsText.overflowMode = TextOverflowModes.Overflow;
    }

    void Update()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
        float fps = 1.0f / deltaTime;
        if (fpsText != null)
            fpsText.text = $"{fps:0} FPS {OVRManager.display?.displayFrequency:0} Hz";
    }
}