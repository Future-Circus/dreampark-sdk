namespace DreamPark
{
    using UnityEngine;
    using System.Collections;
#if UNITY_EDITOR
    using UnityEditor;
#endif
    public class MusicArea : MonoBehaviour
    {
        private static int activeMusicAreas = 0;
        public static int? _priority;
        public static MusicArea currentMusicArea;
        public AudioClip musicTrack;
        public float volume = 0.6f;
        public int priority = 0;
        [ReadOnly] public bool isPlaying = false;
        private bool isActive = false;
        private AudioSource audioSource;
        private Vector3 halfExtents = Vector3.zero;
        private LevelTemplate levelTemplate;
        // public AudioSource AudioSource => audioSource;
        private Coroutine duckCoroutine;
        [HideInInspector] public bool isFocused = false;
        public virtual void Awake()
        {
            if (!musicTrack)
            {
                enabled = false;
                return;
            }
            GameObject musicEmitter = new GameObject("MusicEmitter");
            musicEmitter.transform.parent = transform;
            musicEmitter.transform.localPosition = Vector3.zero;
            musicEmitter.transform.localRotation = Quaternion.identity;
            musicEmitter.transform.localScale = Vector3.one;
            musicEmitter.AddComponent<RealisticRolloff>();
            audioSource = musicEmitter.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.volume = volume;
            audioSource.priority = priority;
            audioSource.clip = musicTrack;
            audioSource.spatialBlend = 1;
            audioSource.volume = 0;

            BodyTracker bodyTracker = musicEmitter.AddComponent<BodyTracker>();
            bodyTracker.yOffset = 2f;

            levelTemplate = GetComponent<LevelTemplate>();
            if (levelTemplate)
            {
                var bounds2D = GameLevelDimensions.GetDimensionsInMeters(levelTemplate.size);
                if (levelTemplate.size == GameLevelSize.Custom)
                {
                    bounds2D = GameLevelDimensions.GetDimensionsInMeters(new Vector2(levelTemplate.customSize.x, levelTemplate.customSize.y));
                }
                halfExtents = new Vector3(bounds2D.x / 2f, 50f, bounds2D.y / 2f);
            }
            else
            {
                halfExtents = transform.localScale * 0.5f;
            }
        }
        void Update()
        {
            if (isPlaying && !audioSource.isPlaying)
            {
                currentMusicArea = null;
                audioSource.enabled = true;
                isPlaying = false;
            }
            if (!levelTemplate)
            {
                halfExtents = transform.localScale * 0.5f;
            }
            if (Camera.main)
            {
                var camPos = Camera.main.transform.position;
#if UNITY_EDITOR
                camPos = SceneView.lastActiveSceneView.camera.transform.position;
#endif
                if (IsPointWithinBounds(Camera.main.transform.position, transform, halfExtents))
                {
                    if (!isActive)
                    {
                        activeMusicAreas++;
                        isActive = true;
                    }
                    if (!isPlaying)
                    {
                        Enter();
                    }
                }
                else
                {
                    if (isActive)
                    {
                        activeMusicAreas--;
                        isActive = false;
                    }
                    if (isPlaying)
                    {
                        Exit();
                    }
                }
            }
        }
        public virtual void Enter()
        {
            if (currentMusicArea)
            {
                if (currentMusicArea != this && currentMusicArea.audioSource != null && audioSource != null && currentMusicArea.audioSource.clip == audioSource.clip)
                {
                    AdoptPlaybackFrom(currentMusicArea);
                    currentMusicArea.isPlaying = false;
                    currentMusicArea = this;
                    isPlaying = true;
                    return;
                }

                if (priority > currentMusicArea.priority)
                {
                    currentMusicArea.Exit();
                }
                else
                {
                    return;
                }
            }
            if (audioSource != null)
            {
                audioSource.PlayWithFadeIn(1f, this, volume);
                currentMusicArea = this;
                isPlaying = true;
            }
        }
        public virtual void Exit()
        {
            if (audioSource != null && activeMusicAreas > 0)
            {
                audioSource.PauseWithFadeOut(1f, this, volume);
                if (currentMusicArea == this)
                {
                    currentMusicArea = null;
                }
                isPlaying = false;
            }
        }
        public void SwapAudioClip(AudioClip newClip, float time = 0.5f)
        {
            if (audioSource == null || newClip == null)
                return;

            if (audioSource.clip == newClip)
            {
                musicTrack = newClip;
                return;
            }

            audioSource.PauseWithFadeOut(time, this, volume);
            this.Wait(time, () =>
            {
                audioSource.clip = newClip;
                musicTrack = newClip;
                if (isPlaying)
                {
                    audioSource.PlayWithFadeIn(time, this, volume);
                }
            });
        }
        private void AdoptPlaybackFrom(MusicArea otherArea)
        {
            if (otherArea == null || otherArea.audioSource == null || audioSource == null)
                return;

            AudioSource otherSource = otherArea.audioSource;
            audioSource.clip = otherSource.clip;
            musicTrack = otherArea.musicTrack;
            audioSource.pitch = otherSource.pitch;
            audioSource.time = otherSource.time;
            audioSource.volume = otherSource.volume;

            if (otherSource.isPlaying)
                audioSource.Play();

            otherSource.Stop();
            otherSource.volume = 0f;
        }
        bool IsPointWithinBounds(Vector3 point, Transform obj, Vector3 halfExtents)
        {
            if (isFocused) {
                return true;
            }
            // Vector from center to point in world space
            Vector3 toPoint = point - obj.position;

            // Project onto the object’s local axes
            float x = Vector3.Dot(toPoint, obj.right);
            float y = Vector3.Dot(toPoint, obj.up);
            float z = Vector3.Dot(toPoint, obj.forward);

            // Check against half extents (which can come directly from localScale * 0.5f)
            return Mathf.Abs(x) <= halfExtents.x &&
                Mathf.Abs(y) <= halfExtents.y &&
                Mathf.Abs(z) <= halfExtents.z;
        }
        void OnDestroy()
        {
            if (currentMusicArea == this)
            {
                currentMusicArea = null;
            }
        }
        public float GetPitch()
        {
            return audioSource.pitch;
        }
        public void SetPitch(float pitch)
        {
            audioSource.pitch = pitch;
        }
        public void Duck(float duckDuration, float fadeInDuration = 0.25f, float fadeOutDuration = 0.25f, float duckVolume = 0.2f)
        {
            if (duckCoroutine != null)
            {
                StopCoroutine(duckCoroutine);
            }
            duckCoroutine = StartCoroutine(DuckCoroutine(duckDuration, fadeInDuration, fadeOutDuration, duckVolume));
        }
        private IEnumerator DuckCoroutine(float duckDuration, float fadeInDuration, float fadeOutDuration, float duckVolume)
        {
            float originalVolume = volume;
            float targetVolume = originalVolume * duckVolume;
            while (audioSource.volume > targetVolume)
            {
                audioSource.volume = Mathf.MoveTowards(audioSource.volume, targetVolume, Time.deltaTime / fadeOutDuration);
                yield return null;
            }
            yield return new WaitForSeconds(duckDuration);
            while (audioSource.volume < originalVolume)
            {
                audioSource.volume = Mathf.MoveTowards(audioSource.volume, originalVolume, Time.deltaTime / fadeInDuration);
                yield return null;
            }
            duckCoroutine = null;
        }
#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            if (!TryGetComponent(out LevelTemplate levelTemplate))
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(transform.position, transform.localScale);
            }
        }
#endif
    }

}
