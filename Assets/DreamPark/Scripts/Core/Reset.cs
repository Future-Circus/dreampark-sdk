namespace SuperAdventureLand
{
    using UnityEngine;
    using UnityEngine.SceneManagement;

    public class Reset : MonoBehaviour
    {
        public void Reload() {
            SceneManager.LoadScene(0);
        }

        private void OnApplicationPause(bool pauseStatus) {
            if (pauseStatus) {
                if (!Application.isEditor) {
                    Reload();
                }
            }
        }
        private void OnApplicationQuit() {
            if (!Application.isEditor) {
                Reload();
            }
        }
    }

}
