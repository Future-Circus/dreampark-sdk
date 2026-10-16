#if UNITY_EDITOR && !DREAMPARKCORE
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DreamPark
{
    /// <summary>
    /// Editor-only gesture source: while `DreamPark → Simulate Phone Player`
    /// is on, the mouse in the Game view behaves like one finger on a phone.
    ///
    ///   left press            → TouchDown
    ///   move past 2% screen   → DragBegin, then DragMove per change
    ///   release               → DragEnd (if dragging), TouchUp, then Tap when
    ///                           held under 0.25 s and moved under 2%
    ///   scroll wheel          → Pinch (scroll up = spread = scale above 1)
    ///
    /// Recognition lives here (and in Core's touch source on device), never in
    /// DreamParkGestures, which only routes. Never compiled into a player.
    /// Compiled out of dreampark-core too: Core's own touch source
    /// (PhoneGestureSource, via Lean Touch's mouse simulation) already feeds
    /// the Editor there, and two sources would double every gesture.
    /// </summary>
    [AddComponentMenu("")]
    public class DreamParkEditorMouseGestures : MonoBehaviour
    {
        public const float TapMaxSeconds = 0.25f;
        public const float MoveThreshold = 0.02f;   // normalized screen units

        static DreamParkEditorMouseGestures _instance;

        bool _down;
        bool _dragging;
        float _downTime;
        Vector2 _downPos;
        Vector2 _lastPos;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("~DreamParkEditorMouseGestures") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DreamParkEditorMouseGestures>();
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        void Update()
        {
            if (!DreamParkDevice.SimulatePhone)
            {
                if (_down) Cancel();
                return;
            }
            if (!ReadMouse(out Vector2 pixels, out bool pressed, out bool released, out bool held, out float scroll))
                return;

            float w = Mathf.Max(1f, Screen.width), h = Mathf.Max(1f, Screen.height);
            Vector2 pos = new Vector2(Mathf.Clamp01(pixels.x / w), Mathf.Clamp01(pixels.y / h));

            if (pressed)
            {
                _down = true;
                _dragging = false;
                _downTime = Time.unscaledTime;
                _downPos = _lastPos = pos;
                Send(DreamParkGestureKind.TouchDown, pos, Vector2.zero);
            }
            else if (_down && held)
            {
                if (!_dragging && (pos - _downPos).magnitude >= MoveThreshold)
                {
                    _dragging = true;
                    Send(DreamParkGestureKind.DragBegin, pos, pos - _downPos);
                    _lastPos = pos;
                }
                else if (_dragging && pos != _lastPos)
                {
                    Send(DreamParkGestureKind.DragMove, pos, pos - _lastPos);
                    _lastPos = pos;
                }
            }

            if (released && _down)
            {
                if (_dragging) Send(DreamParkGestureKind.DragEnd, pos, pos - _lastPos);
                Send(DreamParkGestureKind.TouchUp, pos, Vector2.zero);
                bool quick = Time.unscaledTime - _downTime <= TapMaxSeconds;
                if (!_dragging && quick && (pos - _downPos).magnitude < MoveThreshold)
                    Send(DreamParkGestureKind.Tap, pos, Vector2.zero);
                _down = false;
                _dragging = false;
            }

            if (Mathf.Abs(scroll) > 0.01f)
            {
                // ~1.2× per mouse-wheel notch (120 units); trackpads send
                // smaller steps and get a proportionally smaller pinch.
                float scale = Mathf.Exp(Mathf.Clamp(scroll, -600f, 600f) * 0.0015f);
                DreamParkGestures.Submit(new DreamParkGesture
                {
                    kind = DreamParkGestureKind.Pinch,
                    fingerId = 0,
                    screen = pos,
                    delta = Vector2.zero,
                    pinchScale = scale,
                });
            }
        }

        void Cancel()
        {
            _down = false;
            _dragging = false;
        }

        static void Send(DreamParkGestureKind kind, Vector2 pos, Vector2 delta)
        {
            DreamParkGestures.Submit(new DreamParkGesture
            {
                kind = kind,
                fingerId = 0,
                screen = pos,
                delta = delta,
                pinchScale = 1f,
            });
        }

        static bool ReadMouse(out Vector2 pixels, out bool pressed, out bool released, out bool held, out float scroll)
        {
            pixels = Vector2.zero; pressed = released = held = false; scroll = 0f;
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse == null) return false;
            pixels = mouse.position.ReadValue();
            pressed = mouse.leftButton.wasPressedThisFrame;
            released = mouse.leftButton.wasReleasedThisFrame;
            held = mouse.leftButton.isPressed;
            scroll = mouse.scroll.ReadValue().y;
            return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            pixels = Input.mousePosition;
            pressed = Input.GetMouseButtonDown(0);
            released = Input.GetMouseButtonUp(0);
            held = Input.GetMouseButton(0);
            scroll = Input.mouseScrollDelta.y * 120f;
            return true;
#else
            return false;
#endif
        }
    }
}
#endif
