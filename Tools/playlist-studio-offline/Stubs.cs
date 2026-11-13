// Stand-ins for the Unity-only SDK types ContentPlaylistStore.cs names in its AssetDatabase resolver
// (never executed offline; the tests inject ContentPlaylistValidator.Resolver(...)).
namespace DreamPark
{
    public class AttractionTemplate : UnityEngine.MonoBehaviour
    {
        public enum SizeKind { Custom }
        public SizeKind size;
        public bool HasPackingBake => true;
    }
    public class DreamSequenceTemplate : AttractionTemplate { }
}
namespace DreamPark.Editor
{
    public static class DreamSequenceGenerator
    {
        public static bool IsSpecialLevelPath(string contentId, string path) => false;
    }
}
