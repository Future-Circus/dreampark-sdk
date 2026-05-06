#if UNITY_EDITOR
using System;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Modal popup window for logging into DreamPark. Used by editor panels
    // that gate on AuthAPI.isLoggedIn — the panel shows a "Log in" button that
    // opens this popup, which closes itself on successful login. Subscribers
    // to AuthAPI.LoginStateChanged repaint to reveal panel contents.
    public class AuthPopup : EditorWindow
    {
        private string email = "";
        private string password = "";
        private bool isSubmitting = false;
        private string errorMessage = null;
        private bool focusedOnce = false;

        public static void Show()
        {
            // If a popup is already open, refocus it instead of stacking another.
            var existing = Resources.FindObjectsOfTypeAll<AuthPopup>();
            if (existing != null && existing.Length > 0)
            {
                existing[0].Focus();
                return;
            }

            var win = CreateInstance<AuthPopup>();
            win.titleContent = new GUIContent("Log in to DreamPark");
            win.minSize = new Vector2(360, 180);
            win.maxSize = new Vector2(360, 180);
            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(
                main.x + (main.width - 360) / 2f,
                main.y + (main.height - 180) / 2f,
                360, 180);
            win.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Sign in to DreamPark", EditorStyles.boldLabel);
            GUILayout.Space(4);

            GUI.enabled = !isSubmitting;

            GUI.SetNextControlName("AuthPopup_Email");
            email = EditorGUILayout.TextField("Email", email);

            GUI.SetNextControlName("AuthPopup_Password");
            password = EditorGUILayout.PasswordField("Password", password);

            if (!focusedOnce)
            {
                EditorGUI.FocusTextInControl("AuthPopup_Email");
                focusedOnce = true;
            }

            GUILayout.Space(4);

            if (!string.IsNullOrEmpty(errorMessage))
            {
                EditorGUILayout.HelpBox(errorMessage, MessageType.Error);
            }

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Sign Up"))
            {
                Application.OpenURL("https://dreampark.app/signup");
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel"))
            {
                Close();
            }

            bool canSubmit = !string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password) && email.Contains("@");
            GUI.enabled = !isSubmitting && canSubmit;
            bool clicked = GUILayout.Button(isSubmitting ? "Signing in..." : "Log in");
            bool enterPressed = Event.current.type == EventType.KeyDown
                                 && Event.current.keyCode == KeyCode.Return
                                 && GUI.enabled;
            if (clicked || enterPressed)
            {
                Submit();
                if (enterPressed) Event.current.Use();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void Submit()
        {
            isSubmitting = true;
            errorMessage = null;
            Repaint();

            AuthAPI.Login(email, password, (success, response) =>
            {
                isSubmitting = false;
                if (success)
                {
                    Close();
                }
                else
                {
                    string err = null;
                    if (response?.json != null && response.json.HasField("error"))
                    {
                        err = response.json.GetField("error").stringValue;
                    }
                    errorMessage = !string.IsNullOrEmpty(err)
                        ? err
                        : (response?.error ?? "Login failed. Check your email and password.");
                    Repaint();
                }
            });
        }
    }
}
#endif
