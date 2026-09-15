#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Defective.JSON;
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
        private const string RequiredPackagesAssetPath = "Assets/DreamPark/Resources/RequiredPackages.json";
        private const string ManifestRelativePath = "Packages/manifest.json";

        // Packages this export deliberately does NOT declare as required, even
        // though they're real entries in this repo's own Packages/manifest.json.
        // Pure dev/IDE/test tooling that Assets/DreamPark's own code never
        // references — forcing it onto every creator project would be adding
        // workflow preferences, not fixing a compile break. Exact-match plus one
        // prefix rule (Unity's own built-in engine modules, which every install
        // already has and aren't a "did the creator forget to add a package"
        // case the way a UPM/git dependency is).
        private static readonly HashSet<string> NonRequiredPackages = new HashSet<string>
        {
            "com.boxqkrtm.ide.cursor",
            "com.coplaydev.unity-mcp",
            "com.unity.collab-proxy",
            "com.unity.feature.development",
            "com.unity.ide.rider",
            "com.unity.ide.visualstudio",
            "com.unity.mobile.android-logcat",
            "com.unity.multiplayer.center",
            "com.unity.recorder",
            "com.unity.test-framework",
        };
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

                // 1b. Refresh RequiredPackages.json from THIS project's own
                //     Packages/manifest.json so an existing project updating in
                //     place can be brought up to date too — see
                //     DreamParkPackageSync.cs for why this exists at all and
                //     what it can and cannot guarantee.
                RefreshRequiredPackagesFile(newVersion);

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

        // Regenerates RequiredPackages.json from this project's OWN
        // Packages/manifest.json, filtered by NonRequiredPackages. Runs on every
        // publish so the file exported alongside this SDK version can never go
        // stale relative to whatever Assets/DreamPark actually needs to compile
        // at the moment it's exported — the whole point is that this list is
        // regenerated, not hand-maintained.
        //
        // Best-effort and non-fatal: a failure here must not be able to block a
        // publish over a nice-to-have. Logs a warning and leaves whatever
        // RequiredPackages.json already exists (possibly none) untouched.
        private static void RefreshRequiredPackagesFile(string sdkVersion)
        {
            try
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string manifestFullPath = Path.Combine(projectRoot, ManifestRelativePath);
                if (!File.Exists(manifestFullPath))
                {
                    Debug.LogWarning($"[DreamPark] {ManifestRelativePath} not found — RequiredPackages.json not refreshed.");
                    return;
                }

                var manifest = new JSONObject(File.ReadAllText(manifestFullPath));
                var deps = manifest.GetField("dependencies");
                if (deps == null || deps.type != JSONObject.Type.Object || deps.keys == null)
                {
                    Debug.LogWarning($"[DreamPark] {ManifestRelativePath} has no 'dependencies' object — RequiredPackages.json not refreshed.");
                    return;
                }

                var packages = new JSONObject(JSONObject.Type.Object);
                for (int i = 0; i < deps.keys.Count; i++)
                {
                    string id = deps.keys[i];
                    if (string.IsNullOrEmpty(id) || NonRequiredPackages.Contains(id)) continue;

                    string value = deps.list[i] != null ? deps.list[i].stringValue : null;
                    if (string.IsNullOrEmpty(value)) continue;

                    packages.AddField(id, value);
                }

                var required = new JSONObject(JSONObject.Type.Object);
                required.AddField("version", 1);
                required.AddField("generatedFromSdkVersion", sdkVersion);
                required.AddField("packages", packages);

                string dir = Path.GetDirectoryName(RequiredPackagesAssetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(RequiredPackagesAssetPath, required.Print(true) + "\n");
                AssetDatabase.ImportAsset(RequiredPackagesAssetPath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.SaveAssets();

                Debug.Log($"[DreamPark] RequiredPackages.json refreshed — {packages.keys?.Count ?? 0} package(s).");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not refresh RequiredPackages.json (publish continues): {e.Message}");
            }
        }
    }
}
#endif
