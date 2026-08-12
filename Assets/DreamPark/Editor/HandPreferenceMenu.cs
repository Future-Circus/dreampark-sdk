// ─────────────────────────────────────────────────────────────────────
//  HandPreferenceMenu.cs — test left/right hand tracking in the editor
//
//  DreamPark ▸ Hand Tracking ▸ Left / Right / Both
//
//  On a headset the guest simply raises whichever hand they use, and
//  HandTracker.ChooseHand() follows it. In the editor there is no hand to
//  raise, so which one is "active" has to be stated — that is all this
//  menu does. It writes HandTracker.handPreference, the SAME field the
//  device path reads, rather than introducing an editor-only concept of
//  handedness that content could accidentally start depending on.
//
//  The choice is an EditorPref, not a scene edit: it survives play mode,
//  domain reloads and scene changes, and it never dirties a prefab. It is
//  a property of the person testing, not of the content.
//
//  Applied continuously while playing, because the rig that matters is
//  usually not in the scene when you press Play — Player.prefab is spawned
//  by the park loader (or the Park Simulator) a second or two in, and a
//  one-shot apply would miss it entirely.
// ─────────────────────────────────────────────────────────────────────

using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools
{
    [InitializeOnLoad]
    public static class HandPreferenceMenu
    {
        const string Key = "DreamPark.Editor.HandPreference";

        const string MenuLeft  = "DreamPark/Hand Tracking/Left";
        const string MenuRight = "DreamPark/Hand Tracking/Right";
        const string MenuBoth  = "DreamPark/Hand Tracking/Both (whichever is tracked)";

        // Matches the device default: Both, i.e. let tracking decide.
        public static HandTracker.HandPreference Current
        {
            get { return (HandTracker.HandPreference)EditorPrefs.GetInt(Key, (int)HandTracker.HandPreference.Both); }
            set
            {
                EditorPrefs.SetInt(Key, (int)value);
                ApplyToOpenTrackers();
            }
        }

        static double _nextApply;

        static HandPreferenceMenu()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode) ApplyToOpenTrackers();
            };
        }

        // Cheap poll rather than a hierarchy hook: rig objects arrive from an
        // addressable load, which raises no editor event worth listening to.
        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextApply) return;
            _nextApply = EditorApplication.timeSinceStartup + 0.5;

            if (!EditorApplication.isPlaying) return;
            ApplyToOpenTrackers();
        }

        static void ApplyToOpenTrackers()
        {
            var preference = Current;

            var trackers = Object.FindObjectsByType<HandTracker>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < trackers.Length; i++)
            {
                var tracker = trackers[i];
                if (tracker == null || tracker.handPreference == preference) continue;

                tracker.handPreference = preference;

                // Only mark a SCENE object dirty. Touching a prefab instance's
                // serialized field here would show up as an unapplied override
                // on content the tester never edited.
                if (!EditorApplication.isPlaying) EditorUtility.SetDirty(tracker);
            }
        }

        [MenuItem(MenuLeft, false, 130)]
        static void SetLeft() { Current = HandTracker.HandPreference.Left; }

        [MenuItem(MenuRight, false, 131)]
        static void SetRight() { Current = HandTracker.HandPreference.Right; }

        [MenuItem(MenuBoth, false, 132)]
        static void SetBoth() { Current = HandTracker.HandPreference.Both; }

        [MenuItem(MenuLeft, true)]
        static bool ValidateLeft()
        {
            Menu.SetChecked(MenuLeft, Current == HandTracker.HandPreference.Left);
            return true;
        }

        [MenuItem(MenuRight, true)]
        static bool ValidateRight()
        {
            Menu.SetChecked(MenuRight, Current == HandTracker.HandPreference.Right);
            return true;
        }

        [MenuItem(MenuBoth, true)]
        static bool ValidateBoth()
        {
            Menu.SetChecked(MenuBoth, Current == HandTracker.HandPreference.Both);
            return true;
        }
    }
}
