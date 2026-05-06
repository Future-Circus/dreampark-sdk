using DreamPark;
using UnityEngine;

public class EasyMusicArea : EasyEvent
{
    public enum MusicAreaChangeType {
        DUCK,
        SWAP,
        DISABLE,
        UNFOCUS
    }

    public MusicArea musicArea;
    public MusicAreaChangeType changeType = MusicAreaChangeType.DUCK;
    [ShowIf("changeType", MusicAreaChangeType.DUCK)] public float duckTime = 1f;
    [ShowIf("changeType", MusicAreaChangeType.SWAP)] public AudioClip swapAudio;
    [HideIf("changeType", MusicAreaChangeType.UNFOCUS)] public bool focusMusic = false;
    public static MusicArea focusedMusicArea;
    public override void OnEvent(object arg0 = null) {
        // Handle unfocus first - clear any focused music area
        if (focusedMusicArea != null && (focusMusic || changeType == MusicAreaChangeType.UNFOCUS)) {
            focusedMusicArea.SwapAudioClip(focusedMusicArea.musicTrack);
            focusedMusicArea.isFocused = false;
            focusedMusicArea = null;
            
            // If just unfocusing, we're done
            if (changeType == MusicAreaChangeType.UNFOCUS) {
                onEvent?.Invoke(null);
                return;
            }
        }
        
        // Get music area if not set
        if (musicArea == null) {
            musicArea = MusicArea.currentMusicArea;
            if (musicArea == null) {
                Debug.LogError("no MusicArea linked to this EasyMusicArea");
                onEvent?.Invoke(null);
                return;
            }
        }
        
        // Set focus AFTER getting the music area (not inside the null check)
        if (focusMusic) {
            focusedMusicArea = musicArea;
            focusedMusicArea.isFocused = true;
        }
        
        switch (changeType) {
            case MusicAreaChangeType.DUCK:
                musicArea.Duck(duckTime);
                break;
            case MusicAreaChangeType.SWAP:
                Debug.Log("SwapAudioClip: " + swapAudio.name);
                musicArea.SwapAudioClip(swapAudio);
                break;
            case MusicAreaChangeType.DISABLE:
                musicArea.Exit();
                musicArea.enabled = false;
                break;
            case MusicAreaChangeType.UNFOCUS:
                // Already handled above
                break;
            default:
                Debug.LogError("invalid MusicAreaChangeType");
                break;
        }

        onEvent?.Invoke(null);
    }
}