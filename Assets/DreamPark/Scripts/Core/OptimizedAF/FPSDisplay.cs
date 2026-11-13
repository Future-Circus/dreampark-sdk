using UnityEngine;
using TMPro;

/// Head-locked frame-rate readout. Each second: frames shown, the WORST frame (ms) and how many frames ran
/// late (over 1.5× the display interval) — a smoothed FPS hides the 30–50 ms hitches that read as stutter.
/// Updated once a second (no per-frame string garbage). Core: on with the park flag `fps_counter`
/// (OptimizedAF), or the inspector's showFPS.
public class FPSDisplay : MonoBehaviour
{
    private TMP_Text fpsText;
    private GameObject textObj;
    private float windowStart = -1f;
    private int frames, late;
    private float worst;

    void Update()
    {
        if (fpsText == null && !CreateText()) return;
        float now = Time.realtimeSinceStartup;
        float dt = Time.unscaledDeltaTime;
        if (windowStart < 0f) { windowStart = now; return; }
        float hz = DisplayHz();
        frames++;
        if (dt > worst) worst = dt;
        if (dt > 1.5f / hz) late++;
        if (now - windowStart < 1f) return;
        float fps = frames / (now - windowStart);
        fpsText.text = $"{fps:0} FPS / {hz:0} Hz\nworst {worst * 1000f:0} ms  late {late}";
        fpsText.color = late == 0 ? Color.white : late <= 3 ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 0.4f, 0.35f);
        windowStart = now; frames = 0; late = 0; worst = 0f;
    }

    private bool CreateText()
    {
        Camera cam = Camera.main;
        if (cam == null) return false;
        textObj = new GameObject("FPSText");
        // Nov 11 2026 (Arcadeland playtest M41 / Zombiez M61): the readout was baked into every recorded clip.
        // UI is the layer recorded clips leave out (SessionReelCompositor.ExcludedLayers); the eyes still see it.
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer >= 0) textObj.layer = uiLayer;
        textObj.transform.SetParent(cam.transform, false);
        textObj.transform.localPosition = new Vector3(0, -0.2f, 0.5f);
        textObj.transform.localRotation = Quaternion.identity;
        textObj.transform.localScale = Vector3.one * 0.01f;
        fpsText = textObj.AddComponent<TextMeshPro>();
        fpsText.fontSize = 30;
        fpsText.color = Color.white;
        fpsText.alignment = TextAlignmentOptions.Center;
        fpsText.textWrappingMode = TextWrappingModes.NoWrap;
        fpsText.overflowMode = TextOverflowModes.Overflow;
        fpsText.text = "FPS";
        return true;
    }

    private static float DisplayHz()
    {
#if UNITY_IOS && !UNITY_EDITOR
        float hz = (float)Screen.currentResolution.refreshRateRatio.value; // no OVRManager in the iOS player
#else
        float hz = OVRManager.display != null ? OVRManager.display.displayFrequency : 0f;
        if (hz <= 1f) hz = (float)Screen.currentResolution.refreshRateRatio.value;
#endif
        return hz > 1f ? hz : 60f;
    }

    void OnDestroy()
    {
        if (textObj != null) Destroy(textObj);
    }
}
