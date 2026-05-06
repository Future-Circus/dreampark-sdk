using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
public class CollideAudio : MonoBehaviour
{
    public AudioClip audioClip;

    //editor function that automatically gets audioclip from Assets, Audio/thud if its blank. its not a resource
    #if UNITY_EDITOR
    public void OnValidate()
    {
        if (audioClip == null)
        {
                audioClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Content/SuperAdventureLand/Audio/hit.wav");
            }
    }
    #endif
    
    public void OnCollisionEnter(Collision collision)
    {
        float volume = Mathf.Clamp01(collision.relativeVelocity.magnitude / 10f);
        float pitch = Random.Range(0.8f, 1.2f);
        audioClip.PlaySFX(transform.position, volume, pitch);
    }
}
