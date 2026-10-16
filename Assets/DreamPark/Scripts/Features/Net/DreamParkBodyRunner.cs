using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Internal engine ticker for the Lua-driven body services: <see cref="NetBodies"/>
    /// (dp.net_body) and <see cref="Handhelds"/> (dp.hold). One hidden, self-created object per
    /// session — never authored, never in a prefab, nothing a creator adds or configures.
    /// Created on first use (<see cref="Ensure"/>).
    /// </summary>
    [AddComponentMenu("")]
    [DefaultExecutionOrder(31000)]   // after gameplay scripts and trackers, before PlaytestAvatar (32000)
    public sealed class DreamParkBodyRunner : MonoBehaviour
    {
        static DreamParkBodyRunner _instance;

        /// <summary>Make sure the ticker exists (play mode only; Edit-mode callers tick by hand).</summary>
        public static void Ensure()
        {
            if (_instance != null || !Application.isPlaying) return;
            // HideInHierarchy only (not DontSave): a DontDestroyOnLoad object is torn down when the
            // Editor leaves Play mode, so nothing leaks into edit mode.
            var go = new GameObject("~DreamParkBodies") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DreamParkBodyRunner>();
        }

        void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        void Update() => NetBodies.Tick(Time.time);

        void FixedUpdate() => NetBodies.FixedTick(Time.time, Time.fixedDeltaTime);

        void LateUpdate() => Handhelds.Tick();
    }
}
