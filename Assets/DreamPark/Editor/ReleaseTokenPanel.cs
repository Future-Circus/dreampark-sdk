#if UNITY_EDITOR
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // One unified admin/owner-facing panel for minting a release token — a
    // bypass-login credential (`rlt_...`) that lets a headless/CI caller drive
    // dreampark_content_publish, dreampark_sdk_publish, or the Quest APK
    // release workflow without the interactive email-code sign-in AuthPopup
    // requires. Per Aidan: "This is just a unified token generator interface
    // that we can re-use for content release tokens, sdk release tokens, and
    // core release tokens" — one window, one scope selector, not three panels.
    //
    // Byte-identical across dreampark-sdk and dreampark-core (see
    // Assets/DreamPark/RELEASE_AUTOMATION.md's sync note) — minting a token
    // for any scope is just an authenticated backend call, so it doesn't need
    // the target repo's own code open. Whichever project an admin happens to
    // have open can mint any scope's token.
    //
    // The token is shown once and never persisted by this window — same
    // per-invocation, no-EditorPrefs posture the token itself is designed
    // for (see AuthAPI.WithReleaseToken's comment on why EditorPrefs is the
    // wrong store for this credential class).
    public class ReleaseTokenPanel : EditorWindow
    {
        private ReleaseTokenAPI.Scope scope = ReleaseTokenAPI.Scope.Core;
        private string contentId = "";
        private string label = "";
        private string expiresInDaysText = ""; // blank = server default (90d, max 365)

        private bool isGenerating;
        private string generatedToken;
        private string status;
        private bool statusIsError;

        [MenuItem("DreamPark/Generate Release Token", false, 5)]
        public static void ShowWindow()
        {
            GetWindow<ReleaseTokenPanel>("Release Token");
        }

        // UX-only gate, same posture as SDKPublishPanel.ValidateShowWindow: the
        // window always opens once logged in (a content-owner token needs no
        // admin bit at all — the backend checks contentOwners[] membership
        // instead), the per-scope admin requirement is enforced inside OnGUI
        // and, regardless, re-checked for real on the backend at mint time.
        [MenuItem("DreamPark/Generate Release Token", true, 5)]
        public static bool ValidateShowWindow()
        {
            return AuthAPI.isLoggedIn;
        }

        private void OnEnable()
        {
            AuthAPI.LoginStateChanged += OnLoginStateChanged;
            AdminState.AdminStateChanged += Repaint;
        }

        private void OnDisable()
        {
            AuthAPI.LoginStateChanged -= OnLoginStateChanged;
            AdminState.AdminStateChanged -= Repaint;
        }

        private void OnLoginStateChanged(bool _) => Repaint();

        // Admin scopes (core/sdk) require AdminState.IsAdmin==true, exactly like
        // SDKPublishPanel's gate. Content scope has no local answer — ownership
        // is per-title, not a single global bit — so it's always offered here;
        // a non-owner's mint attempt gets refused server-side (NOT_CONTENT_OWNER).
        private bool RequiresAdmin => scope == ReleaseTokenAPI.Scope.Core || scope == ReleaseTokenAPI.Scope.Sdk;

        private void OnGUI()
        {
            if (!AuthAPI.isLoggedIn)
            {
                EditorGUILayout.HelpBox("Log in to generate release tokens.", MessageType.Info);
                if (GUILayout.Button("Open Sign-In"))
                {
                    AuthPopup.Show();
                }
                return;
            }

            GUILayout.Label("Generate Release Token", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Mints a bypass-login credential a headless release agent uses instead of " +
                "the interactive email-code sign-in. Shown once — copy it now. This window " +
                "never writes it to disk.",
                MessageType.Info);

            GUILayout.Space(8);

            GUI.enabled = !isGenerating;
            var newScope = (ReleaseTokenAPI.Scope)EditorGUILayout.EnumPopup("Scope", scope);
            if (newScope != scope)
            {
                scope = newScope;
                generatedToken = null;
                status = null;
            }

            if (scope == ReleaseTokenAPI.Scope.Content)
            {
                contentId = EditorGUILayout.TextField("Content ID", contentId);
            }

            label = EditorGUILayout.TextField("Label (optional)", label);
            expiresInDaysText = EditorGUILayout.TextField("Expires in days (optional, max 365)", expiresInDaysText);
            GUI.enabled = true;

            if (RequiresAdmin && AdminState.IsAdmin != true)
            {
                EditorGUILayout.HelpBox(
                    AdminState.IsAdmin == null
                        ? "Checking admin access..."
                        : $"{AuthAPI.email} is not authorized to generate {ReleaseTokenAPI.ScopeString(scope)} release tokens.",
                    MessageType.Warning);
            }

            GUILayout.Space(8);

            bool contentIdMissing = scope == ReleaseTokenAPI.Scope.Content && string.IsNullOrWhiteSpace(contentId);
            bool adminBlocked = RequiresAdmin && AdminState.IsAdmin != true;
            GUI.enabled = !isGenerating && !contentIdMissing && !adminBlocked;
            if (GUILayout.Button(isGenerating ? "Generating..." : "Generate Token", GUILayout.Height(32)))
            {
                Generate();
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(generatedToken))
            {
                GUILayout.Space(8);
                EditorGUILayout.LabelField("Token (shown once)");
                EditorGUILayout.SelectableLabel(generatedToken, EditorStyles.textField, GUILayout.Height(20));
                if (GUILayout.Button("Copy to Clipboard"))
                {
                    EditorGUIUtility.systemCopyBuffer = generatedToken;
                    status = "Copied to clipboard.";
                    statusIsError = false;
                }
            }

            if (!string.IsNullOrEmpty(status))
            {
                GUILayout.Space(6);
                EditorGUILayout.HelpBox(status, statusIsError ? MessageType.Error : MessageType.Info);
            }
        }

        private void Generate()
        {
            int? expiresInDays = null;
            if (!string.IsNullOrWhiteSpace(expiresInDaysText))
            {
                if (!int.TryParse(expiresInDaysText.Trim(), out int parsed) || parsed <= 0)
                {
                    status = "Expires in days must be a positive whole number, or left blank.";
                    statusIsError = true;
                    Repaint();
                    return;
                }
                expiresInDays = parsed;
            }

            isGenerating = true;
            status = null;
            generatedToken = null;
            Repaint();

            string mintContentId = scope == ReleaseTokenAPI.Scope.Content ? contentId.Trim() : null;
            ReleaseTokenAPI.Mint(scope, mintContentId, label.Trim(), expiresInDays, (success, response) =>
            {
                isGenerating = false;
                if (success && response?.json != null && response.json.HasField("token"))
                {
                    generatedToken = response.json.GetField("token").stringValue;
                    status = "Generated. This is the only time it will be shown.";
                    statusIsError = false;
                }
                else
                {
                    // Surface the stable code alongside the message where it adds
                    // information the prose might not (e.g. NOT_CONTENT_OWNER makes
                    // "you don't own this title" unambiguous even if the wording changes).
                    string code = ReleaseTokenAPI.ExtractCode(response);
                    string message = ReleaseTokenAPI.ExtractError(response, "Could not generate a release token.");
                    status = string.IsNullOrEmpty(code) ? message : $"{message} ({code})";
                    statusIsError = true;
                }
                Repaint();
            });
        }
    }
}
#endif
