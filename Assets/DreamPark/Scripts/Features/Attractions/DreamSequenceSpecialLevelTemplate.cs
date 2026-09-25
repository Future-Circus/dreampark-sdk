using UnityEngine;

namespace DreamPark
{
    public enum DreamSequenceSpecialLevelRole { Start, Overlay, GameOver }

    /// <summary>Marks an editable spatial level specialized for sequence presentation.</summary>
    public sealed class DreamSequenceSpecialLevelTemplate : MonoBehaviour
    {
        public DreamSequenceSpecialLevelRole role;
        public Vector2 sizeFeet = new Vector2(DreamSequenceTemplate.StandardWidthFeet,
            DreamSequenceTemplate.StandardLengthFeet);
        [Tooltip("Overlay levels remain active while the selected attraction level changes.")]
        public bool persistentAcrossLevels;
    }
}
