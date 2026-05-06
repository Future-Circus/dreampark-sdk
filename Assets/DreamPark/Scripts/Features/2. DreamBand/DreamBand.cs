using UnityEngine;
using Text = TMPro.TMP_Text;
using System;
using System.Collections.Generic;
public enum DreamBandState {
    START,
    STANDBY,
    STANDBYING,
    PLAY,
    PLAYING,
    PAUSE,
    PAUSING,
    END,
    ENDING,
    COLLECT,
    COLLECTING,
    INJURE,
    INJURING,
    RESTART,
    RESTARTING,
    ACHIEVEMENT,
    ACHIEVEMENTING,
    WIN,
    WINNING,
    DESTROY,
    DESTROYING
}
public class DreamBand : StandardEntity<DreamBandState>
{
    public static Dictionary<string, DreamBand> instances;
    [ReadOnly] public string gameId;
    public static DreamBand Instance;
    public Text timerText;
    public override void ExecuteState()
    {
        switch (state) {
            case DreamBandState.START:

                if (instances == null) {
                    instances = new Dictionary<string, DreamBand>();
                }
                if (instances.ContainsKey(gameId)) {
                    Destroy(gameObject);
                    return;
                }
                instances.Add(gameId, this);
                Show();
                SetState(DreamBandState.STANDBY);
                break;
            case DreamBandState.STANDBY:
                break;
            case DreamBandState.STANDBYING:
                if (Mathf.Floor(Time.time) % 2 == 0) {
                    timerText.enabled = false;
                } else {
                    timerText.enabled = true;
                }
                #if DREAMPARK_CORE
                if (SessionTime.Instance != null && SessionTime.Instance.sessionActive) {
                    SetState(DreamBandState.PLAY);
                }
                #endif
                break;
            case DreamBandState.PLAY:
                timerText.enabled = true;
                break;
            case DreamBandState.PLAYING:
                #if DREAMPARK_CORE
                if (SessionTime.Instance != null && SessionTime.Instance.GetSessionTime() > 0) {
                    timerText.text = SessionTime.Instance.GetSessionTimeInMinutes();
                } else {
                    timerText.text = "00:00";
                }
                if (SessionTime.Instance != null && !SessionTime.Instance.sessionActive) {
                    if (SessionTime.Instance.sessionEnded) {
                        SetState(DreamBandState.END);
                    } else {
                        SetState(DreamBandState.STANDBY);
                    }
                }
                #endif
                break;
            case DreamBandState.PAUSE:
                break;
            case DreamBandState.PAUSING:
                if (Mathf.Floor(Time.time) % 2 == 0) {
                    timerText.enabled = false;
                } else {
                    timerText.enabled = true;
                }
                break;
            case DreamBandState.COLLECTING:
                SetState(DreamBandState.PLAY);
                break;
            case DreamBandState.INJURING:
                SetState(DreamBandState.PLAY);
                break;
            case DreamBandState.ACHIEVEMENTING:
                SetState(DreamBandState.PLAY);
                break;
            case DreamBandState.DESTROY:
                Destroy(gameObject);
                break;
            case DreamBandState.END:
                break;
            case DreamBandState.ENDING:
                break;
        }
    }

    public void Show() {
        if (isEnded) {
            return;
        }
        if (Instance) {
            Instance.Hide();
        }
        Instance = this;
        SetState(DreamBandState.PLAY);
    }

    public void Hide() {
        if (isEnded) {
            return;
        }
        SetState(DreamBandState.STANDBY);
    }
    
    public bool isPlaying {
        get {
            return state == DreamBandState.PLAY || state == DreamBandState.PLAYING;
        }
    }
    public bool isPaused {
        get {
            return state == DreamBandState.PAUSE || state == DreamBandState.PAUSING;
        }
    }
    public bool isEnded {
        get {
            return state == DreamBandState.END || state == DreamBandState.ENDING;
        }
    }

    public void OnDestroy() {
        if (DreamBand.instances != null && DreamBand.instances.ContainsKey(gameId)) {
            DreamBand.instances.Remove(gameId);
        }
        if (DreamBand.Instance == this) {
            DreamBand.Instance = null;
        }
    }
}
