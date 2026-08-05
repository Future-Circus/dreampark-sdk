#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Admin-facing panel for shipping a new SDK version. Flow:
    //   1. Validate the new version is semver, > current
    //   2. Write the new version into Resources/DreamParkSDKVersion.json
    //   3. Export Assets/DreamPark/ as a .unitypackage to Temp/
    //   4. POST it to /api/sdk/publish (backend gates non-admins)
    //   5. On success, the publisher commits the version bump to git themselves
    //
    // Non-admins see a clean 403 dialog rather than a hidden menu — admin
    // gating is enforced by the backend, never the frontend.
    public class SDKPublishPanel : EditorWindow
    {
        private const string SDKAssetPath = "Assets/DreamPark";
        private const string VersionResourcePath = "Assets/DreamPark/Resources/DreamParkSDKVersion.json";

        private string newVersion = "";
        private string releaseNotes = "";
        private bool isPublishing = false;
        private string status = null;
        private bool statusIsError = false;

        [MenuItem("DreamPark/Publish SDK Version", false, 2)]
        public static void ShowWindow()
        {
            GetWindow<SDKPublishPanel>("Publish SDK Version");
        }

        // Validate function (the `true` second arg). Unity calls this each time
        // the menu is rebuilt; returning false greys out the item. We require
        // both logged-in AND a positive admin probe — null (unknown) keeps the
        // item disabled. This is purely a UX hint; the publish endpoint
        // re-checks admin access on the backend regardless.
        [MenuItem("DreamPark/Publish SDK Version", true, 2)]
        public static bool ValidateShowWindow()
        {
            return AuthAPI.isLoggedIn && AdminState.IsAdmin == true;
        }

        private void OnEnable()
        {
            AuthAPI.LoginStateChanged += OnLoginStateChanged;
            // Pre-fill version field with a sensible suggestion: bump patch.
            if (string.IsNullOrEmpty(newVersion))
            {
                newVersion = SuggestNextVersion(SDKVersion.Current);
            }
        }

        private void OnDisable()
        {
            AuthAPI.LoginStateChanged -= OnLoginStateChanged;
        }

        private void OnLoginStateChanged(bool _) => Repaint();

        private void OnGUI()
        {
            if (!AuthAPI.isLoggedIn)
            {
                ContentUploaderPanel.DrawLoginGate("Log in to publish SDK versions.");
                return;
            }

            GUILayout.Label("Publish New SDK Version", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Exports Assets/DreamPark/ as a .unitypackage, uploads it, and marks it as the latest version. " +
                "Other projects with the SDK installed will see an update prompt next time they open Unity. " +
                "This is admin-only on the backend.",
                MessageType.Info);

            GUILayout.Space(6);

            EditorGUILayout.LabelField("Current version", SDKVersion.Current);

            GUI.enabled = !isPublishing;
            newVersion = EditorGUILayout.TextField("New version", newVersion);
            EditorGUILayout.LabelField("Release notes");
            releaseNotes = EditorGUILayout.TextArea(releaseNotes, GUILayout.MinHeight(100));
            GUI.enabled = true;

            // Inline validation feedback (shown without blocking the button until
            // submit, so the user can compose freely).
            string validationError = ValidateNewVersion(newVersion);
            if (!string.IsNullOrEmpty(validationError))
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            }

            GUILayout.Space(8);

            GUI.enabled = !isPublishing && string.IsNullOrEmpty(validationError);
            if (GUILayout.Button(isPublishing ? "Publishing..." : "Export & Publish", GUILayout.Height(32)))
            {
                Publish();
            }
            GUI.enabled = true;

            // ── Local testing ──────────────────────────────────────────
            // Smaller secondary action: export Assets/DreamPark/ as a
            // .unitypackage to <ProjectRoot>/Builds/ and reveal it in
            // Finder/Explorer. No version bump, no backend upload — just
            // a fast loop for testing SDK changes in another local Unity
            // project before going through the formal Publish flow.
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Local testing", EditorStyles.miniLabel);
            GUI.enabled = !isPublishing;
            if (GUILayout.Button("Export .unitypackage (local — no upload)", GUILayout.Height(22)))
            {
                ExportLocal();
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(status))
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(status, statusIsError ? MessageType.Error : MessageType.Info);
            }
        }

        // Mirror of Publish()'s export step, minus the version bump and the
        // backend upload. Lands the .unitypackage in the project's Builds/
        // folder (gitignored by default) and pops Finder/Explorer focused on
        // the file so the user can drag it straight into a target project.
        private void ExportLocal()
        {
            try
            {
                status = "Exporting...";
                statusIsError = false;
                Repaint();

                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string buildsDir = Path.Combine(projectRoot, "Builds");
                Directory.CreateDirectory(buildsDir);

                // Timestamp keeps successive exports distinguishable; current
                // SDK version is included so the filename is self-describing
                // when dragged into another project.
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string fileName = $"dreampark-sdk-v{SDKVersion.Current}-{timestamp}.unitypackage";
                string outPath = Path.Combine(buildsDir, fileName);

                AssetDatabase.ExportPackage(SDKAssetPath, outPath, ExportPackageOptions.Recurse);

                if (!File.Exists(outPath))
                {
                    FailWith("Export failed — .unitypackage was not created.");
                    return;
                }

                long sizeKB = new FileInfo(outPath).Length / 1024;
                Debug.Log($"[DreamPark] Exported SDK to {outPath} ({sizeKB} KB)");
                EditorUtility.RevealInFinder(outPath);

                status = $"✅ Exported {fileName} ({sizeKB} KB). Revealed in Finder.";
                statusIsError = false;
                Repaint();
            }
            catch (Exception e)
            {
                FailWith("Export failed: " + e.Message);
            }
        }

        private static string SuggestNextVersion(string current)
        {
            if (SDKVersion.TryParse(current, out int maj, out int min, out int patch))
            {
                return $"{maj}.{min}.{patch + 1}";
            }
            return "0.1.0";
        }

        private string ValidateNewVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "Enter a new version (e.g. 1.4.2).";
            if (!SDKVersion.TryParse(version, out _, out _, out _))
                return "Version must be MAJOR.MINOR.PATCH (e.g. 1.4.2).";
            if (SDKVersion.Compare(version, SDKVersion.Current) <= 0)
                return $"New version must be greater than current ({SDKVersion.Current}).";
            return null;
        }

        private void Publish()
        {
            isPublishing = true;
            status = "Bumping version file and exporting package...";
            statusIsError = false;
            Repaint();

            // Capture the pre-bump version for the failure rollback below. This MUST
            // be read before step 1 writes the new value: the rollback used to read
            // SDKVersion.Current at failure time, by which point step 1 had already
            // advanced it to newVersion — so the "rollback" re-committed the bump.
            // ValidateNewVersion then forced the next attempt to bump again, and a
            // couple of failed publishes left the shipped .unitypackage's JSON ahead
            // of the backend manifest. A consumer installing that package reads a
            // Current GREATER than LatestVersion, and every comparison in
            // SDKUpdateChecker is `>= 0` → "up to date" → update prompting silently
            // disabled forever.
            string previousVersion = SDKVersion.Current;

            try
            {
                // 1. Update the version JSON on disk so the exported package
                //    contains the right version. Importer in another project
                //    overwrites this file, atomically setting their local version.
                File.WriteAllText(VersionResourcePath, BuildVersionJson(newVersion));
                // SaveAssets + Refresh + ForceSynchronousImport — belt-and-suspenders
                // sync of the new file content into Unity's asset database before
                // ExportPackage reads from it. Without this, the exported .unitypackage
                // can occasionally contain the previous version of the JSON because
                // ExportPackage reads from Unity's serialized state, not raw disk.
                AssetDatabase.ImportAsset(VersionResourcePath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                SDKVersion.Reload();
                Debug.Log($"[DreamPark] Publishing v{newVersion}. Local JSON now reads: {SDKVersion.Current}");

                // 2. Export as .unitypackage to Temp/.
                string tempPath = Path.Combine(Path.GetTempPath(), $"dreampark-sdk-v{newVersion}.unitypackage");
                AssetDatabase.ExportPackage(SDKAssetPath, tempPath, ExportPackageOptions.Recurse);

                if (!File.Exists(tempPath))
                {
                    RollBackVersionFile(previousVersion);
                    FailWith("Export failed — .unitypackage was not created.");
                    return;
                }

                byte[] bytes = File.ReadAllBytes(tempPath);
                string fileName = Path.GetFileName(tempPath);
                status = $"Uploading {bytes.Length / 1024} KB to backend...";
                Repaint();

                // 3. Upload. Server validates admin access and that version > prior latest.
                SDKAPI.PublishVersion(newVersion, releaseNotes, bytes, fileName, (success, response) =>
                {
                    isPublishing = false;
                    if (success)
                    {
                        status = $"✅ Published v{newVersion}. Commit DreamParkSDKVersion.json bump to dreampark-sdk.";
                        statusIsError = false;
                        Debug.Log($"[DreamPark] SDK v{newVersion} published successfully.");
                    }
                    else
                    {
                        string err = SDKAPI.ExtractError(response, "Publish failed.");
                        if (response != null && response.statusCode == 403)
                        {
                            EditorUtility.DisplayDialog("Admin access required",
                                err + "\n\nIf this is wrong, ask another admin to grant you access in admin_access.",
                                "OK");
                        }
                        // Roll back the local version bump so they can try again from a clean state.
                        RollBackVersionFile(previousVersion);
                        FailWith("Publish failed: " + err);
                    }
                    Repaint();
                });
            }
            catch (Exception e)
            {
                // Anything that throws between the bump and a successful upload leaves
                // the working tree carrying a version that was never published.
                RollBackVersionFile(previousVersion);
                FailWith("Export failed: " + e.Message);
            }
        }

        // Restores the version JSON to `previousVersion`. Always pass the value
        // captured BEFORE the bump — never SDKVersion.Current, which by this point is
        // the bumped value.
        private static void RollBackVersionFile(string previousVersion)
        {
            if (string.IsNullOrEmpty(previousVersion)) return;

            // "0.0.0" is SDKVersion's could-not-read fallback, not a real version.
            // Writing it would ship a package that compares older than everything and
            // let ValidateNewVersion accept any number at all.
            if (previousVersion == "0.0.0")
            {
                Debug.LogWarning("[DreamPark] Skipping the version rollback — the pre-bump version "
                               + "could not be read (reported 0.0.0). Restore "
                               + "DreamParkSDKVersion.json from version control before publishing again.");
                return;
            }

            try
            {
                File.WriteAllText(VersionResourcePath, BuildVersionJson(previousVersion));
                AssetDatabase.ImportAsset(VersionResourcePath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                SDKVersion.Reload();
                Debug.Log($"[DreamPark] Rolled the local version file back to v{SDKVersion.Current}.");
            }
            catch (Exception rollbackEx)
            {
                Debug.LogWarning($"[DreamPark] Failed to roll back version file: {rollbackEx.Message}");
            }
        }

        private void FailWith(string message)
        {
            isPublishing = false;
            status = message;
            statusIsError = true;
            Repaint();
        }

        private static string BuildVersionJson(string version)
        {
            // Hand-formatted JSON to keep the file diff stable and reviewer-friendly.
            return "{\n  \"version\": \"" + version + "\",\n  \"builtAt\": null\n}\n";
        }
    }
}
#endif
