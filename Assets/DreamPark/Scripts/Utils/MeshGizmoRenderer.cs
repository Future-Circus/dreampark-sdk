using UnityEngine;

public class MeshGizmoRenderer : MonoBehaviour
{
    [SerializeField] private Mesh mesh;
    [SerializeField] private Color color = Color.white;

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (mesh == null) {
            return;
        }

        Matrix4x4 oldMatrix = Gizmos.matrix;
        Color oldColor = Gizmos.color;

        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = color;
        Gizmos.DrawMesh(mesh, Vector3.zero);

        Gizmos.color = oldColor;
        Gizmos.matrix = oldMatrix;
    }
#endif
}
