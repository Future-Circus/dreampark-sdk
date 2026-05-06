#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools
{
    /// <summary>
    /// Moves freshly-imported .unitypackage contents into the active park's
    /// `Assets/Content/<Pascal>/ThirdPartyLocal/` folder.
    ///
    /// Why this exists: `.unitypackage` files hardcode their import paths in
    /// metadata, so Unity always lands them at `Assets/<VendorName>/` regardless
    /// of what folder was selected at import time. The DreamPark convention is
    /// to have package contents under each park's ThirdPartyLocal/. This utility
    /// physically moves the folders post-import, preserving GUIDs (so all
    /// material/prefab/asset references stay intact).
    ///
    /// Usage:
    ///   - Right-click a vendor folder at Assets/ root → DreamPark > Move to ThirdPartyLocal
    ///   - DreamPark > Relocate ALL stray packages (top menu — batch fix)
    ///   - Static: PackageRelocator.MoveFolderToThirdPartyLocal(string folderAssetPath, string parkName = null)
    /// </summary>
    public static class PackageRelocator
    {
        // Standard Unity / DreamPark / vendor-SDK folders that should NEVER be moved.
        // Anything else at Assets/ root is a candidate for relocation IF it also
        // passes the content-based safety check (LooksLikeImportedAssetPack below).
        //
        // When adding a new SDK that drops a top-level folder, add its folder name
        // here. The content-based check is a safety net but the allowlist is the
        // first-class way to declare intent.
        static readonly HashSet<string> ProtectedTopLevelFolders = new HashSet<string>
        {
            // DreamPark
            "Content",          // park-specific content (the canonical location)
            "DreamPark",        // SDK
            // Unity-managed
            "Settings",         // URP/HDRP/Quality
            "Plugins",          // Unity-managed
            "Resources",        // Unity-managed
            "Scenes",           // legacy / SDK scenes
            "Editor",           // editor scripts
            "Editor Default Resources",
            "Gizmos",
            "StreamingAssets",
            "WebGLTemplates",
            "TextMesh Pro",
            "TextMeshPro",
            "Tools",
            "AddressableAssetsData",
            // XR / Meta SDK family
            "XR",                       // XR Plug-in Management settings
            "XRI",                      // XR Interaction Toolkit
            "XR Interaction Toolkit",
            "MetaXR",                   // Meta XR SDK (the big one — DO NOT MOVE)
            "MetaXRBuildingBlocks",
            "MetaXR.Recorder",
            "Oculus",                   // legacy Meta SDK folder name
            // URP / package samples / common drops
            "URPDefaultResources",
            "UniversalRP",
            "Samples",                  // Unity package samples land here
            "Samples~",
            "Recordings",
        };

        // Content-based safety check — even if a folder isn't in the allowlist,
        // these markers indicate it's NOT a stray vendor asset pack and should be
        // left alone. Catches new SDKs that haven't been added to the allowlist yet.
        // Returns false (skip — leave it alone) if the folder contains any sentinel.
        static bool LooksLikeImportedAssetPack(string folderAssetPath)
        {
            string fullPath = Path.Combine(Application.dataPath, "..", folderAssetPath);
            // UPM-style package.json at the root → not a vendor asset pack
            if (File.Exists(Path.Combine(fullPath, "package.json"))) return false;
            // .asmdef at root + Runtime/Editor split → SDK-style organization
            if (Directory.GetFiles(fullPath, "*.asmdef", SearchOption.TopDirectoryOnly).Length > 0
                && Directory.Exists(Path.Combine(fullPath, "Runtime"))
                && Directory.Exists(Path.Combine(fullPath, "Editor"))) return false;
            // Meta XR sentinels
            if (Directory.Exists(Path.Combine(fullPath, "BuildingBlocks"))) return false;
            if (Directory.Exists(Path.Combine(fullPath, "Core")) && Directory.Exists(Path.Combine(fullPath, "Editor"))) return false;
            // Default: assume it IS a vendor pack candidate
            return true;
        }

        // ─── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Moves a folder (e.g., Assets/RPGMonsterBundlePBR) into the active park's
        /// ThirdPartyLocal/. If parkName is null, auto-detects from Assets/Content/.
        /// Returns the new folder path on success, null on failure.
        /// </summary>
        public static string MoveFolderToThirdPartyLocal(string folderAssetPath, string parkName = null)
        {
            if (string.IsNullOrEmpty(folderAssetPath) || !AssetDatabase.IsValidFolder(folderAssetPath))
            {
                Debug.LogError($"[PackageRelocator] Not a valid folder: {folderAssetPath}");
                return null;
            }

            string folderName = Path.GetFileName(folderAssetPath);
            if (ProtectedTopLevelFolders.Contains(folderName))
            {
                Debug.LogWarning($"[PackageRelocator] '{folderName}' is a protected top-level folder, refusing to move.");
                return null;
            }

            // Defensive content-based check — catches SDK folders not yet in the
            // allowlist (e.g., a new Meta XR module at Assets/SomeNewMetaThing/).
            if (!LooksLikeImportedAssetPack(folderAssetPath))
            {
                Debug.LogWarning($"[PackageRelocator] '{folderName}' looks like an SDK / package.json folder, refusing to move. " +
                                 $"If this is genuinely a vendor asset pack, add it to the allowlist or move it manually.");
                return null;
            }

            // Resolve target ThirdPartyLocal
            string targetTPL = ResolveThirdPartyLocal(parkName);
            if (targetTPL == null)
            {
                Debug.LogError($"[PackageRelocator] Could not resolve target ThirdPartyLocal/. " +
                               $"Pass parkName explicitly, or ensure Assets/Content/<Park>/ exists.");
                return null;
            }

            // Ensure target ThirdPartyLocal exists
            if (!AssetDatabase.IsValidFolder(targetTPL))
            {
                CreateFolderRecursive(targetTPL);
            }

            string newPath = $"{targetTPL}/{folderName}";

            // Handle name collision — append numeric suffix
            if (AssetDatabase.IsValidFolder(newPath))
            {
                int suffix = 2;
                while (AssetDatabase.IsValidFolder($"{targetTPL}/{folderName}_{suffix}")) suffix++;
                newPath = $"{targetTPL}/{folderName}_{suffix}";
                Debug.LogWarning($"[PackageRelocator] '{folderName}' already exists in {targetTPL}, renaming to {Path.GetFileName(newPath)}");
            }

            string error = AssetDatabase.MoveAsset(folderAssetPath, newPath);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError($"[PackageRelocator] MoveAsset failed: {error}");
                return null;
            }

            Debug.Log($"[PackageRelocator] ✓ Moved '{folderAssetPath}' → '{newPath}' (GUIDs preserved)");
            return newPath;
        }

        /// <summary>
        /// Returns paths of folders at Assets/ root that are likely stranded
        /// imported packages (i.e., not in the protected list).
        /// </summary>
        public static List<string> FindStrayPackageFolders()
        {
            var result = new List<string>();
            string assetsRoot = "Assets";
            foreach (var sub in Directory.GetDirectories(Application.dataPath))
            {
                string folderName = Path.GetFileName(sub);
                if (folderName.StartsWith(".")) continue;
                if (ProtectedTopLevelFolders.Contains(folderName)) continue;

                string assetPath = $"{assetsRoot}/{folderName}";
                // Content-based safety net — skip SDK / package folders even if
                // their name isn't in the allowlist yet.
                if (!LooksLikeImportedAssetPack(assetPath)) continue;

                result.Add(assetPath);
            }
            return result;
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        static string ResolveThirdPartyLocal(string parkName)
        {
            // If parkName given, use it
            if (!string.IsNullOrEmpty(parkName))
            {
                string pascal = ToPascalCase(parkName);
                return $"Assets/Content/{pascal}/ThirdPartyLocal";
            }

            // Auto-detect: there should be exactly one folder under Assets/Content/
            string contentDir = "Assets/Content";
            if (!AssetDatabase.IsValidFolder(contentDir)) return null;

            var subfolders = AssetDatabase.GetSubFolders(contentDir);
            if (subfolders.Length == 0)
            {
                Debug.LogError($"[PackageRelocator] Assets/Content/ is empty — can't auto-detect park.");
                return null;
            }
            if (subfolders.Length > 1)
            {
                Debug.LogWarning($"[PackageRelocator] Multiple parks under Assets/Content/ ({string.Join(", ", subfolders.Select(Path.GetFileName))}). " +
                                 $"Using the first one: {subfolders[0]}. Pass parkName explicitly to override.");
            }
            return $"{subfolders[0]}/ThirdPartyLocal";
        }

        static void CreateFolderRecursive(string assetPath)
        {
            // Walk up until we find an existing folder, then create downward
            var parts = assetPath.Split('/');
            string built = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{built}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(built, parts[i]);
                }
                built = next;
            }
        }

        static string ToPascalCase(string parkName)
        {
            // "haunted-mansion" → "HauntedMansion"
            var parts = parkName.Split(new[] { '-', '_', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(parts.Select(p => char.ToUpper(p[0]) + p.Substring(1).ToLower()));
        }

        // ─── Menu items ──────────────────────────────────────────────────────

        // Right-click on a folder at Assets/ root
        [MenuItem("Assets/DreamPark/Move to ThirdPartyLocal", priority = 51)]
        static void MenuMoveSelected()
        {
            int moved = 0;
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (AssetDatabase.IsValidFolder(path))
                {
                    var newPath = MoveFolderToThirdPartyLocal(path);
                    if (!string.IsNullOrEmpty(newPath)) moved++;
                }
            }
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog(
                "DreamPark Package Relocator",
                $"Moved {moved} folder(s) into the active park's ThirdPartyLocal/.",
                "OK");
        }

        [MenuItem("Assets/DreamPark/Move to ThirdPartyLocal", true)]
        static bool ValidateMoveSelected()
        {
            // Show only when at least one selected item is a folder NOT already
            // inside a ThirdPartyLocal/ and NOT in the protected list.
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path)) continue;
                if (path.Contains("/ThirdPartyLocal/")) continue;
                string folderName = Path.GetFileName(path);
                if (ProtectedTopLevelFolders.Contains(folderName)) continue;
                return true;
            }
            return false;
        }

        // Top-bar batch fix — when there are many strays, this is faster than
        // right-clicking each one. Kept on the top bar (not Assets/) since it
        // operates without a specific selection.
        [MenuItem("DreamPark/Relocate ALL stray packages to ThirdPartyLocal", priority = 110)]
        static void MenuRelocateAll()
        {
            var strays = FindStrayPackageFolders();
            if (strays.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "DreamPark Package Relocator",
                    "No stray packages found at Assets/ root. Nothing to relocate.",
                    "OK");
                return;
            }

            string preview = string.Join("\n  ", strays);
            bool ok = EditorUtility.DisplayDialog(
                "Relocate stray packages?",
                $"Found {strays.Count} folder(s) at Assets/ root that look like imported packages:\n\n  {preview}\n\nMove them all into the active park's ThirdPartyLocal/?",
                "Move all",
                "Cancel");
            if (!ok) return;

            int moved = 0;
            foreach (var p in strays)
            {
                if (!string.IsNullOrEmpty(MoveFolderToThirdPartyLocal(p))) moved++;
            }
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog(
                "DreamPark Package Relocator",
                $"Moved {moved} of {strays.Count} folder(s) into ThirdPartyLocal/.",
                "OK");
        }
    }
}
#endif
