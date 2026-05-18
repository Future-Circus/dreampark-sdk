#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Adds a "DreamPark/Documentation" entry to the editor menu bar. Priority
    // 2000 puts it at the very bottom of the DreamPark menu — comfortably
    // below the highest existing top-level items (Troubleshooting at ~208)
    // and the Diagnostics submenu (which has children at 1000-1001). Unity
    // automatically inserts a separator line above a menu item whose
    // priority is more than 10 higher than the previous item, so this gap
    // guarantees the visual spacer the user expects above "Documentation".
    internal static class DocumentationMenuItem
    {
        private const string DocsUrl = "https://dreampark.app/docs";

        [MenuItem("DreamPark/Documentation", false, 2000)]
        public static void OpenDocumentation()
        {
            Application.OpenURL(DocsUrl);
        }
    }
}
#endif
