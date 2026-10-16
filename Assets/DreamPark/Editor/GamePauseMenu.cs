#if UNITY_EDITOR
using UnityEditor;

namespace DreamPark.EditorTools
{
    /// <summary>
    /// Try the operator / parent GAME PAUSE in Play mode (Oct 2026, GamePause.cs):
    /// freezes registered park content exactly as a game master's pause does on a
    /// headset and fires onpause() / onresume() in every Lua script, so a creator
    /// can check that their game holds and resumes cleanly.
    /// </summary>
    static class GamePauseMenu
    {
        [MenuItem("DreamPark/Multiplayer/Game Master Pause (test)", false, 60)]
        static void Pause() => GamePause.Pause(new GamePause.Info
            { byName = "Editor", byRole = "editor", reason = "Testing the pause", source = "editor" });

        [MenuItem("DreamPark/Multiplayer/Game Master Pause (test)", true)]
        static bool CanPause() => EditorApplication.isPlaying && !GamePause.IsPaused;

        [MenuItem("DreamPark/Multiplayer/Game Master Resume (test)", false, 61)]
        static void Resume() => GamePause.Resume("editor");

        [MenuItem("DreamPark/Multiplayer/Game Master Resume (test)", true)]
        static bool CanResume() => EditorApplication.isPlaying && GamePause.IsPaused;
    }
}
#endif
