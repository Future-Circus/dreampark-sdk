using UnityEngine;

[CreateAssetMenu(fileName = "OptimizationSettings", menuName = "ScriptableObjects/OptimizationSettings")]
public class OptimizationSettings : ScriptableObject
{
    public float[] distanceBands = new float[] { 10f, 20f, 100f };
    public float frameInterval = 60f;
    public bool controlRenderers = true;
    public bool controlAnimators = true;
    public bool controlAudioSources = true;
    public bool controlLights = true;
    public bool controlParticles = true;
    public bool controlRigidbodies = true;
    public bool controlColliders = true;
    public bool controlComponents = true;
    public bool disableTest = false;
    [Range(0.0f, 1.0f)] public float resolutionScaleFactor = 0.7f;
    public string[] ignoreTags = new string[] { "Untagged", "MainCamera", "Player", "Ground", "Occluder" };
    public string[] ignoreLayers = new string[] { "Triggers" };

}
