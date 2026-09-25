using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Links an independently placed Adventure occurrence or Player to its
    /// package owner without parenting it under a non-spatial Game Manager.
    /// </summary>
    public sealed class DreamParkPackageMembership : MonoBehaviour
    {
        public DreamParkPackageHost host;
        public string packageInstanceId;
        public string occurrenceId;
    }
}
