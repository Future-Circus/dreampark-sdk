#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

[InitializeOnLoad]
public static class GlobalSceneKeyHandler
{
    static GlobalSceneKeyHandler()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private static void OnSceneGUI(SceneView sceneView)
    {
        // Only respond during play mode
        if (!Application.isPlaying) return;

        Event e = Event.current;
        if (e != null && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Space)
            {
                Debug.Log("Space pressed globally in Scene View");

                // Access your runtime singleton or find object
                Simulator controller = Object.FindFirstObjectByType<Simulator>();
                controller.ReceiveInput();

                e.Use(); // prevent other tools from using the key
            } else if (e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9) {
                Debug.Log($"Number key {e.keyCode - KeyCode.Alpha0} pressed globally in Scene View");
                Simulator controller = Object.FindFirstObjectByType<Simulator>();
                controller.ReceiveOptionInput(e.keyCode - KeyCode.Alpha0);
                e.Use(); // prevent other tools from using the key
            }
        }
    }
}

public class Simulator : MonoBehaviour
{
    private GameObject leftHand;
    private GameObject rightHand;
    private GameObject leftAnchor;
    private GameObject rightAnchor;

    private void Awake()
    {
        // Ensure this only runs in the Editor
        if (!Application.isEditor)
            return;

        // Check if this scene is the root loaded scene
        if (IsRootLoadedScene())
        {
            SpawnCamera();
            SpawnHands();
        } else {
            Destroy(gameObject);
        }
    }

    private bool IsRootLoadedScene()
    {
        return EditorSceneManager.sceneCount <= 2;
    }

    private void SpawnCamera()
    {
        GameObject cameraObject = Camera.main ? Camera.main.gameObject : FindFirstObjectByType<Camera>()?.gameObject;
        OVRManager ovr = FindFirstObjectByType<OVRManager>();

        if (cameraObject == null || cameraObject.tag != "MainCamera") {
            cameraObject = new GameObject("Editor Camera");
            Camera cam = cameraObject.AddComponent<Camera>();
            cam.cullingMask &= ~(1 << LayerMask.NameToLayer("Gizmo"));
            cam.cullingMask &= ~(1 << LayerMask.NameToLayer("SecondaryRenderer"));
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.gray;
            cameraObject.tag = "MainCamera";
            UpdateCamera();
        }      
        cameraObject.layer = LayerMask.NameToLayer("Player");

        if (ovr == null) {
            Componentizer.DoComponent<AudioListener>(cameraObject,true);
            Rigidbody rb = Componentizer.DoComponent<Rigidbody>(cameraObject,true);
            Componentizer.DoComponent<SphereCollider>(cameraObject,true);
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeAll;   
        }
        
        GameObject directionalLight = FindFirstObjectByType<Light>()?.gameObject;
        if (directionalLight == null) {
            directionalLight = new GameObject("Directional Light");
            Light light = directionalLight.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
        }
    }

    private void SpawnHands() {

        OVRHand[] existingHands = FindObjectsByType<OVRHand>(FindObjectsSortMode.InstanceID);
        if (existingHands != null && existingHands.Length > 0) {
            leftHand = existingHands[0].gameObject;
            rightHand = existingHands[1].gameObject;
            leftAnchor = leftHand.transform.parent.gameObject;
            rightAnchor = rightHand.transform.parent.gameObject;
        } else {
            leftAnchor = new GameObject("LeftHandAnchor");
            rightAnchor = new GameObject("RightHandAnchor");
            leftHand = new GameObject("Left Hand");
            rightHand = new GameObject("Right Hand");
            leftHand.AddComponent<OVRHand>();
            rightHand.AddComponent<OVRHand>();
            leftHand.transform.parent = leftAnchor.transform;
            rightHand.transform.parent = rightAnchor.transform;
            leftHand.transform.localPosition = Vector3.zero;
            rightHand.transform.localPosition = Vector3.zero;
            leftHand.transform.localRotation = Quaternion.identity;
            rightHand.transform.localRotation = Quaternion.identity;
            leftHand.transform.localScale = Vector3.one;
            rightHand.transform.localScale = Vector3.one;
        }
    }

    public void UpdateCamera () {        
        SceneView sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null || sceneView.camera == null)
        {
            return;
        }

        if (Camera.main) {
            Camera.main.transform.position = sceneView.camera.transform.position;
            Camera.main.transform.rotation = sceneView.camera.transform.rotation;
        }
    }
    public void UpdateHands() {
       if (leftAnchor != null) {
            leftAnchor.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 0.5f + Camera.main.transform.right * -0.2f;
            leftAnchor.transform.rotation = Camera.main.transform.rotation;
       }
       if (rightAnchor != null) {
            rightAnchor.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 0.5f + Camera.main.transform.right * 0.2f;
            rightAnchor.transform.rotation = Camera.main.transform.rotation;
       }
    }

    public void Update () {
        UpdateCamera();
        UpdateHands();
    }

    public virtual void ReceiveInput() {
        
    }

    public virtual void ReceiveOptionInput(int option = 0) {
        
    }
}
#endif