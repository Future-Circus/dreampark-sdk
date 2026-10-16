using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Lets an extra camera (a shot camera, a minimap, a mirror) see <see cref="PortalScene"/>s directly (Oct 2026,
    /// golf-portal). While this camera renders, the listed scenes (or every active scene when the list is empty) are
    /// visible to it; every other camera still never draws them. Works with cameras rendered by hand (Camera.Render,
    /// e.g. from Lua) and with enabled cameras.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class PortalSceneCamera : MonoBehaviour
    {
        [Tooltip("Scenes this camera sees. Empty = every active PortalScene.")]
        public List<PortalScene> scenes = new List<PortalScene>();

        public static readonly List<PortalSceneCamera> Active = new List<PortalSceneCamera>();
        internal Camera cam;

        void OnEnable()
        {
            cam = GetComponent<Camera>();
            if (!Active.Contains(this)) Active.Add(this);
            PortalRenderer.Ensure();
        }

        void OnDisable() { Active.Remove(this); }

        internal bool Sees(PortalScene s) { return scenes == null || scenes.Count == 0 || scenes.Contains(s); }
    }
}
