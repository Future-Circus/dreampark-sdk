#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using System.Text.RegularExpressions;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Popup for renaming a content folder under Assets/Content/. Used in two flows:
    //   1. The placeholder folder YOUR_GAME_HERE is selected — user must set a real ID.
    //   2. An existing folder has an invalid name (e.g. dashes from a manual rename)
    //      that would break Addressables/upload paths.
    //
    // Validation is intentionally strict: alphanumeric only, must start with a letter.
    // No spaces, dashes, underscores, dots, or any other punctuation. This matches
    // PascalCase conventions ("CoinCollector") while still allowing camelCase/lowercase.
    public class ContentIdSetupPopup : EditorWindow
    {
        // Public so ContentUploaderPanel can use the same regex for inline gating.
        // ^[A-Za-z]    must start with a letter (no leading digits — Addressables choke)
        // [A-Za-z0-9]* only letters and digits afterwards
        // {2,64}       length 2-64 (enforced separately to give a clearer error message)
        public static readonly Regex ContentIdRegex = new Regex(@"^[A-Za-z][A-Za-z0-9]*$");

        public const string PlaceholderName = "YOUR_GAME_HERE";

        private string oldFolderName;
        private string newName = "";
        private bool focusedOnce = false;
        private string errorMessage = null;
        private bool isCheckingBackend = false;
        private Action<string> onRenamed;

        public static bool IsValid(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length < 2 || name.Length > 64) return false;
            return ContentIdRegex.IsMatch(name);
        }

        // Returns a user-friendly explanation of why a name is invalid, or null
        // if it's valid. The popup's live feedback uses this string verbatim.
        public static string ExplainInvalid(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Enter a name (e.g. SuperAdventureLand).";
            if (name.Length < 2) return "Must be at least 2 characters.";
            if (name.Length > 64) return "Must be 64 characters or fewer.";
            if (!char.IsLetter(name[0])) return "Must start with a letter.";
            // Find the first offending character so the error points at it.
            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) || c > 127)
                {
                    return $"'{c}' is not allowed. Use only letters (a-z, A-Z) and digits (0-9).";
                }
            }
            return null;
        }

        public static void Show(string oldFolderName, Action<string> onRenamed)
        {
            // Refocus existing instance instead of stacking a second popup.
            var existing = Resources.FindObjectsOfTypeAll<ContentIdSetupPopup>();
            if (existing != null && existing.Length > 0)
            {
                existing[0].Focus();
                return;
            }

            var win = CreateInstance<ContentIdSetupPopup>();
            bool isPlaceholder = oldFolderName == PlaceholderName;
            win.titleContent = new GUIContent(isPlaceholder ? "Set Content ID" : "Fix Folder Name");
            win.oldFolderName = oldFolderName;
            // Pre-fill with the existing name unless it's the placeholder.
            win.newName = isPlaceholder ? "" : SuggestSafeName(oldFolderName);
            win.onRenamed = onRenamed;
            win.minSize = new Vector2(420, 220);
            win.maxSize = new Vector2(420, 220);
            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(
                main.x + (main.width - 420) / 2f,
                main.y + (main.height - 220) / 2f,
                420, 220);
            win.ShowUtility();
        }

        // Strip invalid chars from a name to give the user a head-start when
        // fixing an existing folder. Doesn't auto-prepend a letter though —
        // we want the user to confirm the new name explicitly.
        private static string SuggestSafeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);
            }
            return sb.ToString();
        }

        private void OnGUI()
        {
            bool isPlaceholder = oldFolderName == PlaceholderName;

            EditorGUILayout.LabelField(
                isPlaceholder ? "Please give your game an ID" : "Fix folder name",
                EditorStyles.boldLabel);

            string explainer = isPlaceholder
                ? "Your game folder is still the SDK template (YOUR_GAME_HERE). Choose a unique ID — it becomes the folder name under Assets/Content/ and the contentId on the backend."
                : $"The folder '{oldFolderName}' contains characters that break uploads (dashes, spaces, or punctuation). Pick a clean name to rename it.";
            EditorGUILayout.LabelField(explainer, EditorStyles.wordWrappedLabel);

            GUILayout.Space(8);
            EditorGUILayout.LabelField("Example: SuperAdventureLand", EditorStyles.miniLabel);

            GUI.SetNextControlName("ContentIdSetup_Input");
            newName = EditorGUILayout.TextField("New ID", newName);
            if (!focusedOnce)
            {
                EditorGUI.FocusTextInControl("ContentIdSetup_Input");
                focusedOnce = true;
            }

            string validation = ExplainInvalid(newName);
            // Also catch the case where the new name is the same as old (no-op).
            if (string.IsNullOrEmpty(validation) && newName == oldFolderName)
            {
                validation = "New name is the same as the current folder name.";
            }
            // Catch collision with an existing folder under Assets/Content/.
            if (string.IsNullOrEmpty(validation) && FolderExists(newName))
            {
                validation = $"A folder named '{newName}' already exists in Assets/Content/.";
            }

            if (!string.IsNullOrEmpty(validation))
            {
                EditorGUILayout.HelpBox(validation, MessageType.Warning);
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                EditorGUILayout.HelpBox(errorMessage, MessageType.Error);
            }

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            GUI.enabled = !isCheckingBackend;
            if (GUILayout.Button("Cancel"))
            {
                Close();
            }
            GUILayout.FlexibleSpace();

            bool canSubmit = string.IsNullOrEmpty(validation) && !isCheckingBackend;
            GUI.enabled = canSubmit;
            string buttonLabel = isCheckingBackend
                ? "Checking..."
                : (isPlaceholder ? "Create" : "Rename");
            bool clicked = GUILayout.Button(buttonLabel);
            bool enterPressed = Event.current.type == EventType.KeyDown
                                 && Event.current.keyCode == KeyCode.Return
                                 && GUI.enabled;
            if (clicked || enterPressed)
            {
                StartRename();
                if (enterPressed) Event.current.Use();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private static bool FolderExists(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return false;
            string path = Path.Combine(Application.dataPath, "Content", folderName);
            return Directory.Exists(path);
        }

        // Gate the rename on a backend ownership check. The user must NOT be able
        // to "claim" a contentId that's owned by someone else just by renaming a
        // local folder — uploads against that folder would 403, and worse, naive
        // code might leak the existence/ownership of foreign content. So:
        //   200 → we already own this contentId (rare but valid, e.g. recreating
        //         a local folder for content we own); allow.
        //   404 → the contentId is unclaimed; allow (AddContent will create on first upload).
        //   403 → owned by another user; reject with a generic message that does
        //         NOT confirm the owner's identity or any PII.
        //   other → treat as transient failure and refuse to rename, since we
        //         can't tell whether the name is safe to claim.
        //
        // We don't even attempt this check if the user isn't logged in (skip
        // straight to the local rename) because the caller has presumably
        // already ensured they're authenticated before opening this popup.
        private void StartRename()
        {
            errorMessage = null;

            if (!AuthAPI.isLoggedIn)
            {
                ApplyLocalRename();
                return;
            }

            isCheckingBackend = true;
            Repaint();

            ContentAPI.GetContent(newName, (success, response) =>
            {
                isCheckingBackend = false;

                if (success)
                {
                    // 200 — we own it (the GET endpoint returns 403 if the caller
                    // isn't in contentOwners, so success implies ownership).
                    ApplyLocalRename();
                    return;
                }

                long code = response != null ? response.statusCode : 0;
                if (code == 404)
                {
                    ApplyLocalRename();
                    return;
                }
                if (code == 403)
                {
                    // Generic message — no email, no owner identity, no other PII.
                    errorMessage = "This name is already taken by another project. Choose a different name.";
                    Repaint();
                    return;
                }

                // 401 / 500 / network — refuse rather than risk a bad rename.
                errorMessage = "Could not verify the name with the backend. Try again in a moment.";
                Repaint();
            });
        }

        private void ApplyLocalRename()
        {
            string oldAssetPath = $"Assets/Content/{oldFolderName}";
            // AssetDatabase.RenameAsset takes the new *leaf* name only.
            string err = AssetDatabase.RenameAsset(oldAssetPath, newName);
            if (!string.IsNullOrEmpty(err))
            {
                errorMessage = $"Rename failed: {err}";
                Repaint();
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Notify the panel so it can refresh its dropdown and select the new id.
            try { onRenamed?.Invoke(newName); }
            catch (Exception e) { Debug.LogWarning($"[DreamPark] ContentIdSetupPopup callback threw: {e}"); }
            Close();
        }
    }
}
#endif
