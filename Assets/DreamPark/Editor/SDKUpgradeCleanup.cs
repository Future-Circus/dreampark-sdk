#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Removes SDK-owned assets that were retired by a newer SDK release.
    ///
    /// Unitypackage imports overwrite and add files, but they never delete files
    /// that disappeared from the package. That is especially dangerous for
    /// generated C# (for example XLua wrappers): a stale wrapper can reference a
    /// generated method that no longer exists and prevent the newly imported SDK
    /// from compiling.
    ///
    /// This migration runs immediately before ImportPackage, while the currently
    /// installed SDK assembly is still alive. Paths are exact, SDK-scoped asset
    /// paths; directories and wildcards are deliberately rejected. Files are
    /// backed up under Library so an interactive import cancellation can restore
    /// the old, internally consistent SDK.
    /// </summary>
    internal static class SDKUpgradeCleanup
    {
        private const string PendingBackupRootKey = "DreamPark.SDKUpgradeCleanup.BackupRoot";
        private const string PendingAssetPathsKey = "DreamPark.SDKUpgradeCleanup.AssetPaths";

        // Versioned local migrations cover known transitions even when the update
        // manifest predates the optional `retiredAssets` field. Add exact files to
        // a new entry whenever an SDK release stops shipping an asset.
        private sealed class RetiredAssetMigration
        {
            public readonly string id;
            public readonly string[] assetPaths;

            public RetiredAssetMigration(string id, params string[] assetPaths)
            {
                this.id = id;
                this.assetPaths = assetPaths;
            }
        }

        private static readonly RetiredAssetMigration[] LocalMigrations =
        {
            new RetiredAssetMigration(
                "2026-09-retire-xlua-example-wrappers",
                "Assets/DreamPark/ThirdParty/XLua/Gen/Tutorial_BaseClassWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/Tutorial_DerivedClassExtensionsWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/Tutorial_DerivedClassWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/Tutorial_ICalcWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_BaseTestWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_Foo1ChildWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_Foo1ParentWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_Foo2ChildWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_Foo2ParentWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_FooExtensionWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_FooWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_LuaBehaviourWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_MyStructWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_NoGcWrap.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_PeddingWrap.cs"),

            // XLua's demo SOURCES (removed from the SDK in May 2026, never deleted from
            // older projects by an import) plus the interface bridges generated for them.
            // While the sources existed, any local codegen regenerated the wrappers above,
            // so retiring only the wrappers could never stick.
            new RetiredAssetMigration(
                "2026-09-retire-xlua-example-sources",
                "Assets/DreamPark/ThirdParty/XLua/Gen/Tutorial_CSCallLua_ItfDBridge.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_IExchangerBridge.cs",
                "Assets/DreamPark/ThirdParty/XLua/Gen/XLuaTest_InvokeLua_ICalcBridge.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/01_Helloworld/Helloworld.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/01_Helloworld/Helloworld.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/01_Helloworld/HelloworldSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/02_U3DScripting/LuaBehaviour.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/02_U3DScripting/LuaTestScript.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/02_U3DScripting/U3DScripting.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/02_U3DScripting/U3DScriptingSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/03_UIEvent/ButtonInteraction.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/03_UIEvent/UI.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/03_UIEvent/UISettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/04_LuaObjectOrented/InvokeLua.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/04_LuaObjectOrented/InvokeLua.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/04_LuaObjectOrented/InvokeLuaSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/05_NoGc/NoGc.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/05_NoGc/NoGc.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/05_NoGc/NoGcSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/06_Coroutine/Coroutine.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/06_Coroutine/CoroutineSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/06_Coroutine/CoroutineTest.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/06_Coroutine/Coroutine_Runner.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/06_Coroutine/Resources/coruntine_test.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/06_Coroutine/Resources/cs_coroutine.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/07_AsyncTest/AsyncTest.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/07_AsyncTest/AsyncTest.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/07_AsyncTest/AsyncTestSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/07_AsyncTest/MessageBox.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/07_AsyncTest/Resources/AlertBox.prefab",
                "Assets/DreamPark/ThirdParty/XLua/Examples/07_AsyncTest/Resources/ConfirmBox.prefab",
                "Assets/DreamPark/ThirdParty/XLua/Examples/07_AsyncTest/Resources/async_test.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/07_AsyncTest/Resources/message_box.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/08_Hotfix/Editor/HotfixTestCfg.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/08_Hotfix/HotfixTest.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/08_Hotfix/HotfixTest.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/08_Hotfix/HotfixTest2.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/08_Hotfix/HotfixTest2.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/08_Hotfix/HotfixTest2Settings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/08_Hotfix/HotfixTestSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/08_Hotfix/StatefullTest.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/09_GenericMethod/Foo.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/09_GenericMethod/GenericMethod.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/09_GenericMethod/GenericMethodExample.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/09_GenericMethod/GenericMethodSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/Resources/signatured1.lua.bytes",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/Resources/signatured2.lua.bytes",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/Resources/signatured3.lua.bytes",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/SignatureLoaderTest.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/SignatureLoaderTest.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/SignatureLoaderTestSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/otherfiles/key_ras",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/otherfiles/key_ras.pub",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/otherfiles/signatured1.lua",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/otherfiles/signatured2.lua",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/otherfiles/signatured3.lua",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/signatured1.lua",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/signatured2.lua",
                "Assets/DreamPark/ThirdParty/XLua/Examples/10_SignatureLoader/signatured3.lua",
                "Assets/DreamPark/ThirdParty/XLua/Examples/11_RawObject/RawObject.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/11_RawObject/RawObjectSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/11_RawObject/RawObjectTest.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/12_ReImplementInLua/ReImplementInLua.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/12_ReImplementInLua/ReImplementInLua.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/12_ReImplementInLua/ReImplementInLuaSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Examples/13_BuildFromCLI/Editor/BuildFromCLI.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/13_BuildFromCLI/build.bat",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/HotfixAsyncAwaitTest.cs",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/HotfixAsyncAwaitTest.unity",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/AsyncMethod1.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/AsyncMethod2.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/AsyncMethod3.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/AsyncMethod4.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/Method1.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/MyTask.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/MyTask1.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/MyTask2.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/14_HotfixAsyncAwait/Resources/await.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Examples/ExampleGenConfig.cs",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/CSharpCallLua/CSCallLua.cs",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/CSharpCallLua/CSCallLua.unity",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/CSharpCallLua/CSCallLuaSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/ByFile/ByFile.cs",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/ByFile/ByFile.unity",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/ByFile/ByFileSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/ByFile/Resources/byfile.lua.txt",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/ByString/ByString.cs",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/ByString/ByString.unity",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/ByString/ByStringSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/Loader/CustomLoader.cs",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/Loader/CustomLoader.unity",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LoadLuaScript/Loader/CustomLoaderSettings.lighting",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LuaCallCSharp/LuaCallCs.cs",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LuaCallCSharp/LuaCallCs.unity",
                "Assets/DreamPark/ThirdParty/XLua/Tutorial/LuaCallCSharp/LuaCallCsSettings.lighting"),

            // The agent kit (helper scripts, a smoke builder and their playbook docs) was
            // staged into Assets/DreamPark/Docs by Sept-Oct 2026 exports and is no longer shipped.
            // DreamParkSdkDocs also deletes the emptied folders.
            new RetiredAssetMigration(
                "2026-10-retire-agent-kit",
                "Assets/DreamPark/Docs/AgentKit/agent/capture.sh",
                "Assets/DreamPark/Docs/AgentKit/agent/lua_surface.py",
                "Assets/DreamPark/Docs/AgentKit/agent/lualint.sh",
                "Assets/DreamPark/Docs/AgentKit/agent/playlock.sh",
                "Assets/DreamPark/Docs/AgentKit/agent/u.sh",
                "Assets/DreamPark/Docs/AgentKit/agent/unity-clone.sh",
                "Assets/DreamPark/Docs/AgentKit/agent/unity-slot.families.example",
                "Assets/DreamPark/Docs/AgentKit/agent/unity-slot.sh",
                "Assets/DreamPark/Docs/AgentKit/agent/xlua_check.py",
                "Assets/DreamPark/Docs/AgentKit/Builders/SmokeBuilder.cs.txt",
                "Assets/DreamPark/Docs/Tools/agent/PLAYBOOK.md",
                "Assets/DreamPark/Docs/Tools/agent/STARTER_PROMPT.md")
        };

        // Published with every SDK release. The backend stores this cumulative
        // list beside the immutable package and returns it from /api/sdk/manifest,
        // allowing an already-installed updater to clean up files retired by a
        // newer release before that release's code is imported.
        //
        // It also lists every file this release ships in XLua/Gen/. Gen/ is one generated
        // unit that must be REPLACED, never merged (see XLuaGenGuard), and listing it
        // here makes an installed updater that predates XLuaGenGuard delete the old
        // Gen/ before the import lays down the new one.
        internal static string[] GetPublishedRetiredAssetPaths()
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            for (int i = 0; i < LocalMigrations.Length; i++)
            {
                string[] paths = LocalMigrations[i].assetPaths;
                for (int j = 0; j < paths.Length; j++)
                {
                    if (unique.Add(paths[j])) result.Add(paths[j]);
                }
            }
            foreach (string path in XLuaGenGuard.GenAssetPaths())
            {
                if (unique.Add(path)) result.Add(path);
            }
            return result.ToArray();
        }

        internal static bool PrepareForUpgrade(IEnumerable<string> releaseRetiredAssets, out string error)
        {
            error = null;

            // A second click while an interactive import is open must not replace
            // the backup required to cancel the first one safely.
            if (HasPendingBackup())
            {
                error = "A previous SDK cleanup transaction is still pending. Cancel or finish that import first.";
                return false;
            }

            var paths = CollectAndValidatePaths(releaseRetiredAssets, out error);
            if (paths == null) return false;

            var existingPaths = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                if (File.Exists(ToAbsolutePath(paths[i]))) existingPaths.Add(paths[i]);
            }

            if (existingPaths.Count == 0) return true;

            string backupRoot = Path.Combine(
                ProjectRoot,
                "Library",
                "DreamParkSDKUpgradeBackup",
                Guid.NewGuid().ToString("N"));

            try
            {
                for (int i = 0; i < existingPaths.Count; i++)
                    BackupAsset(existingPaths[i], backupRoot);

                SessionState.SetString(PendingBackupRootKey, backupRoot);
                SessionState.SetString(PendingAssetPathsKey, string.Join("\n", existingPaths.ToArray()));

                var deleted = new List<string>();
                // Batched: Gen/ alone is ~100 scripts, and one refresh per delete would
                // recompile repeatedly before the import even starts.
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 0; i < existingPaths.Count; i++)
                    {
                        string assetPath = existingPaths[i];
                        if (!AssetDatabase.DeleteAsset(assetPath) && File.Exists(ToAbsolutePath(assetPath)))
                            throw new IOException("Unity could not delete retired asset: " + assetPath);
                        deleted.Add(assetPath);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                Debug.Log(
                    $"[DreamPark] SDK upgrade cleanup removed {deleted.Count} retired asset(s) before import:\n" +
                    string.Join("\n", deleted.ToArray()));
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                RestorePendingBackup();
                return false;
            }
        }

        internal static void CommitPendingUpgrade()
        {
            string backupRoot = SessionState.GetString(PendingBackupRootKey, "");
            ClearPendingState();

            if (!IsSafeBackupRoot(backupRoot) || !Directory.Exists(backupRoot)) return;

            try
            {
                Directory.Delete(backupRoot, true);
            }
            catch (Exception e)
            {
                // Library is disposable and excluded from source control. A backup
                // cleanup failure should not turn a successful SDK update into one.
                Debug.LogWarning($"[DreamPark] Could not remove SDK upgrade backup '{backupRoot}': {e.Message}");
            }
        }

        internal static void RestorePendingBackup()
        {
            string backupRoot = SessionState.GetString(PendingBackupRootKey, "");
            string serializedPaths = SessionState.GetString(PendingAssetPathsKey, "");

            if (!IsSafeBackupRoot(backupRoot) || string.IsNullOrEmpty(serializedPaths))
            {
                ClearPendingState();
                return;
            }

            string[] paths = serializedPaths.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    string assetPath = paths[i];
                    if (!IsSafeRetiredAssetPath(assetPath)) continue;

                    RestoreFileIfMissing(Path.Combine(backupRoot, assetPath), ToAbsolutePath(assetPath));
                    RestoreFileIfMissing(Path.Combine(backupRoot, assetPath + ".meta"), ToAbsolutePath(assetPath + ".meta"));
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Debug.Log($"[DreamPark] Restored {paths.Length} retired asset(s) because the SDK import was cancelled.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DreamPark] Could not restore the cancelled SDK upgrade backup: {e.Message}");
            }
            finally
            {
                CommitPendingUpgrade();
            }
        }

        private static List<string> CollectAndValidatePaths(IEnumerable<string> releaseRetiredAssets, out string error)
        {
            error = null;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();

            for (int i = 0; i < LocalMigrations.Length; i++)
            {
                RetiredAssetMigration migration = LocalMigrations[i];
                for (int j = 0; j < migration.assetPaths.Length; j++)
                {
                    if (!TryAddPath(migration.assetPaths[j], unique, result, out error))
                    {
                        error = $"Invalid path in SDK migration '{migration.id}': {error}";
                        return null;
                    }
                }
            }

            // Replaced wholesale on every update: the whole of XLua/Gen/ (the package
            // brings the complete, matching set) and anything left in the retired XLua
            // demo folders. Enumerated file-by-file so every path still passes the
            // exact-path validation below.
            var wholesale = new List<string>(XLuaGenGuard.GenAssetPaths());
            wholesale.AddRange(XLuaGenGuard.RetiredFolderFileAssetPaths());
            foreach (string path in wholesale)
            {
                if (!TryAddPath(path, unique, result, out error))
                {
                    error = "Invalid generated/retired XLua path: " + error;
                    return null;
                }
            }

            if (releaseRetiredAssets != null)
            {
                foreach (string path in releaseRetiredAssets)
                {
                    if (!TryAddPath(path, unique, result, out error))
                    {
                        error = "Invalid retiredAssets entry in the SDK release manifest: " + error;
                        return null;
                    }
                }
            }

            return result;
        }

        private static bool TryAddPath(
            string path,
            HashSet<string> unique,
            List<string> result,
            out string error)
        {
            error = null;
            if (!IsSafeRetiredAssetPath(path))
            {
                error = string.IsNullOrEmpty(path) ? "path is empty" : path;
                return false;
            }

            if (AssetDatabase.IsValidFolder(path))
            {
                error = path + " is a directory; retired assets must be listed file-by-file";
                return false;
            }

            if (unique.Add(path)) result.Add(path);
            return true;
        }

        private static bool IsSafeRetiredAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!path.StartsWith("Assets/DreamPark/", StringComparison.Ordinal)) return false;
            if (path.Contains("..") || path.Contains("\\") || path.Contains("*") ||
                path.Contains("\n") || path.Contains("\r") || path.EndsWith("/", StringComparison.Ordinal))
                return false;
            return true;
        }

        private static void BackupAsset(string assetPath, string backupRoot)
        {
            CopyFile(ToAbsolutePath(assetPath), Path.Combine(backupRoot, assetPath));

            string metaPath = assetPath + ".meta";
            string absoluteMetaPath = ToAbsolutePath(metaPath);
            if (File.Exists(absoluteMetaPath))
                CopyFile(absoluteMetaPath, Path.Combine(backupRoot, metaPath));
        }

        private static void CopyFile(string source, string destination)
        {
            string parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(source, destination, true);
        }

        private static void RestoreFileIfMissing(string source, string destination)
        {
            if (!File.Exists(source) || File.Exists(destination)) return;
            CopyFile(source, destination);
        }

        private static bool HasPendingBackup()
        {
            return !string.IsNullOrEmpty(SessionState.GetString(PendingBackupRootKey, ""));
        }

        private static bool IsSafeBackupRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string expectedRoot = Path.GetFullPath(Path.Combine(ProjectRoot, "Library", "DreamParkSDKUpgradeBackup")) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            return candidate.StartsWith(expectedRoot, StringComparison.Ordinal);
        }

        private static void ClearPendingState()
        {
            SessionState.EraseString(PendingBackupRootKey);
            SessionState.EraseString(PendingAssetPathsKey);
        }

        private static string ToAbsolutePath(string assetPath)
        {
            return Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }
    }
}
#endif
