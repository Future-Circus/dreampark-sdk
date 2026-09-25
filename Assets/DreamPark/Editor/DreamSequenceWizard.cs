#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    internal static class DreamSequenceGenerator
    {
        private const string StartButtonMeshPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/StartButton/Sphere.001.mesh";
        private const string StartButtonMaterialPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/StartButton/Start.mat";
        private const string ElevatorArrowTexturePath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Overlay/elevator-arrow-up.png";
        private const string ElevatorArrowMaterialPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Overlay/Elevator Arrow.mat";
        internal const string StartButtonPrefabPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/StartButton/Start Button.prefab";
        internal const string ElevatorControlsPrefabPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Overlay/Elevator Controls.prefab";
        internal const string TransitionEffectPrefabPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Overlay/Transition/Level Transition Effect.prefab";
        internal const string BorderPrefabPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Border/Sequence Floor Border.prefab";
        private const string BorderScaffoldStamp = "DreamSequenceDefaultBorderV1";
        private const string FollowerScaffoldStamp = "DreamSequenceDefaultFollowerV1";
        private const string StartControlScaffoldStamp = "DreamSequenceStartControlV1";
        private const string NavigationScaffoldStamp = "DreamSequenceNavigationV1";
        private const string TransitionScaffoldStamp = "DreamSequenceTransitionV1";
        private const string EmptyGameOverShrinkStamp = "DreamSequenceEmptyGameOverShrinkV1";
        private const string LevelDefaultsScaffoldStamp = "DreamSequenceLevelDefaultsV1";

        public static string SequencePrefabPath(string contentId)
            => $"Assets/Content/{contentId}/DreamSequence/Dream Sequence.prefab";
        public static string ContainerPrefabPath(string contentId)
            => $"Assets/Content/{contentId}/Prefabs/Game Container.prefab";
        public static string StartLevelPath(string contentId)
            => $"Assets/Content/{contentId}/DreamSequence/Start Level.prefab";
        public static string OverlayLevelPath(string contentId)
            => $"Assets/Content/{contentId}/DreamSequence/Overlay Level.prefab";
        public static string GameOverLevelPath(string contentId)
            => $"Assets/Content/{contentId}/DreamSequence/Game Over Level.prefab";

        public static bool IsSpecialLevelPath(string contentId, string assetPath)
        {
            if (string.IsNullOrEmpty(contentId) || string.IsNullOrEmpty(assetPath)) return false;
            return string.Equals(assetPath, StartLevelPath(contentId), StringComparison.OrdinalIgnoreCase)
                || string.Equals(assetPath, OverlayLevelPath(contentId), StringComparison.OrdinalIgnoreCase)
                || string.Equals(assetPath, GameOverLevelPath(contentId), StringComparison.OrdinalIgnoreCase);
        }

        public static bool NeedsScaffoldRefresh(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return false;
            return AssetDatabase.LoadAssetAtPath<GameObject>(ContainerPrefabPath(contentId)) == null
                || NeedsLevelRefresh(StartLevelPath(contentId), DreamSequenceSpecialLevelRole.Start)
                || NeedsLevelRefresh(OverlayLevelPath(contentId), DreamSequenceSpecialLevelRole.Overlay)
                || NeedsLevelRefresh(GameOverLevelPath(contentId), DreamSequenceSpecialLevelRole.GameOver);
        }

        // A title owns one Container, whether it has an Adventure, Sequence, or both.
        // Recompiling a package never edits this prefab or its Lua source.
        public static string EnsureContainer(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return null;
            string contentRoot = $"Assets/Content/{contentId}";
            string scripts = EnsureFolder(contentRoot, "Scripts");
            string prefabs = EnsureFolder(contentRoot, "Prefabs");
            string scriptPath = $"{scripts}/game-container.lua.txt";
            WriteStarterTextAsset(scriptPath, ContainerLua);
            string prefabPath = $"{prefabs}/Game Container.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
                return prefabPath;

            var root = new GameObject("Game Manager");
            try
            {
                // The manager owns title-wide Lua state and must not be culled
                // when the player walks away from any individual attraction.
                // Keep this marker on the manager only, never on its package root.
                root.AddComponent<OptimizedAFIgnore>();
                // The Container is the one shared bus for sequence navigation.
                root.AddComponent<NetId>();
                var lua = root.AddComponent<LuaBehaviour>();
                lua.luaScript = AssetDatabase.LoadAssetAtPath<TextAsset>(scriptPath);
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null)
                    throw new InvalidOperationException($"Could not save Game Container at '{prefabPath}'.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            return prefabPath;
        }

        public static void EnsureScaffold(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return;
            string contentRoot = $"Assets/Content/{contentId}";
            string scripts = EnsureFolder(contentRoot, "Scripts");
            string materials = EnsureFolder(contentRoot, "Materials");
            string buttonPath = $"{scripts}/dreamsequence-DreamSequence-button.lua.txt";
            EnsureContainer(contentId);
            WriteStarterTextAsset(buttonPath, ButtonLua);
            Material material = GetOrCreateMaterial($"{materials}/DreamSequenceDefault.mat");
            Material arrowMaterial = AssetDatabase.LoadAssetAtPath<Material>(ElevatorArrowMaterialPath)
                ?? GetOrCreateArrowMaterial($"{materials}/DreamSequenceElevatorArrow.mat");
            EnsureLevelPrefabs(contentRoot, material, arrowMaterial,
                AssetDatabase.LoadAssetAtPath<TextAsset>(buttonPath),
                out _, out _, out _);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureLevelPrefabs(string contentRoot, Material material, Material arrowMaterial,
            TextAsset buttonScript,
            out string startPath, out string overlayPath, out string gameOverPath)
        {
            string folder = EnsureFolder(contentRoot, "DreamSequence");
            startPath = $"{folder}/Start Level.prefab";
            overlayPath = $"{folder}/Overlay Level.prefab";
            gameOverPath = $"{folder}/Game Over Level.prefab";

            EnsureLevelPrefab(startPath, "Start Level", DreamSequenceSpecialLevelRole.Start,
                material, arrowMaterial, buttonScript);
            EnsureLevelPrefab(overlayPath, "Overlay Level", DreamSequenceSpecialLevelRole.Overlay,
                material, arrowMaterial, buttonScript);
            EnsureLevelPrefab(gameOverPath, "Game Over Level", DreamSequenceSpecialLevelRole.GameOver,
                material, arrowMaterial, buttonScript);
        }

        private static void EnsureLevelPrefab(string path, string name, DreamSequenceSpecialLevelRole role,
            Material material, Material arrowMaterial, TextAsset buttonScript)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset != null && !NeedsLevelRefresh(path, role)) return;

            bool created = asset == null;
            bool addDefaultBorder = AssetDatabase.LoadAssetAtPath<GameObject>(BorderPrefabPath) != null
                && !HasBorderScaffoldStamp(path);
            bool migrateBorderFollower = !HasScaffoldStamp(path, FollowerScaffoldStamp);
            bool migrateStartControl = role == DreamSequenceSpecialLevelRole.Start
                && !HasScaffoldStamp(path, StartControlScaffoldStamp);
            bool migrateNavigation = role == DreamSequenceSpecialLevelRole.Overlay
                && !HasScaffoldStamp(path, NavigationScaffoldStamp);
            bool migrateTransition = role == DreamSequenceSpecialLevelRole.Overlay
                && !HasScaffoldStamp(path, TransitionScaffoldStamp);
            bool migrateEmptyGameOver = role == DreamSequenceSpecialLevelRole.GameOver
                && !HasScaffoldStamp(path, EmptyGameOverShrinkStamp);
            bool migrateLevelDefaults = !HasScaffoldStamp(path, LevelDefaultsScaffoldStamp);
            GameObject root = created ? new GameObject(name) : PrefabUtility.LoadPrefabContents(path);
            try
            {
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
                ConfigureAttractionLevel(root, migrateLevelDefaults);
                RemoveChild(root.transform, "12 ft × 18 ft Level Floor");
                RemoveChild(root.transform, "LevelFloor");
                RemoveChild(root.transform, "Game Over Backdrop");

                if (role == DreamSequenceSpecialLevelRole.Start)
                {
                    Transform button = FindStartButton(root.transform);
                    if (migrateStartControl && button != null && !UsesReusableStartButton(button))
                    {
                        UnityEngine.Object.DestroyImmediate(button.gameObject);
                        button = null;
                    }
                    if (button == null && migrateStartControl)
                    {
                        CreateExtractedStartButton(root.transform, material, buttonScript);
                        button = FindStartButton(root.transform);
                    }
                    if (button != null && migrateStartControl)
                        button.localPosition = StartButtonGroundPosition(button);
                }
                else if (role == DreamSequenceSpecialLevelRole.Overlay)
                {
                    Transform navigation = FindChildRecursive(root.transform, "Default 3D Level Navigation");
                    if (navigation == null && migrateNavigation)
                        CreateNavigation(root.transform, material, arrowMaterial, buttonScript);
                    else if (migrateNavigation && navigation != null && NeedsNavigationRefresh(navigation))
                    {
                        UnityEngine.Object.DestroyImmediate(navigation.gameObject);
                        CreateNavigation(root.transform, material, arrowMaterial, buttonScript);
                    }
                    else if (migrateNavigation && NeedsNavigationFacingRefresh(navigation))
                    {
                        // Rotate the old default in place so prefab overrides survive.
                        navigation.localRotation = Quaternion.Euler(0f, -90f, 0f);
                    }
                    if (migrateTransition && FindChildRecursive(root.transform, "Default Level Transition") == null)
                        CreateTransitionEffect(root.transform);
                }
                Transform border = FindChildRecursive(root.transform, "Sequence Floor Border");
                if (addDefaultBorder)
                {
                    border ??= CreateDefaultBorder(root.transform);
                    RecalculateBorderBounds(border);
                }
                // Upgrade existing special levels as well as newly generated ones.
                // The separate stamp lets developers subsequently disable or remove
                // this follower without the scaffold silently restoring it.
                if (border != null && migrateBorderFollower)
                    EnsureBorderFollower(border);

                // The border is a packing follower, not a shrink blocker. Bake
                // its pose into the level before the prefab is first saved so a
                // generated Sequence works without a separate refresh action.
                AttractionTemplate level = root.GetComponent<AttractionTemplate>();
                if (level == null || !AttractionPackingBaker.Bake(level, false))
                    throw new InvalidOperationException($"Could not bake Sequence level '{name}'.");
                if (role == DreamSequenceSpecialLevelRole.GameOver)
                    AllowEmptySpecialLevelShrink(level);

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                if (created) UnityEngine.Object.DestroyImmediate(root);
                else PrefabUtility.UnloadPrefabContents(root);
            }
            if (addDefaultBorder) StampBorderScaffold(path);
            if (migrateBorderFollower) StampScaffold(path, FollowerScaffoldStamp);
            if (migrateStartControl) StampScaffold(path, StartControlScaffoldStamp);
            if (migrateNavigation) StampScaffold(path, NavigationScaffoldStamp);
            if (migrateTransition) StampScaffold(path, TransitionScaffoldStamp);
            if (migrateEmptyGameOver) StampScaffold(path, EmptyGameOverShrinkStamp);
            if (migrateLevelDefaults) StampScaffold(path, LevelDefaultsScaffoldStamp);
        }

        private static void ConfigureAttractionLevel(GameObject root, bool setStarterDefaults)
        {
            foreach (DreamSequenceSpecialLevelTemplate marker in
                root.GetComponents<DreamSequenceSpecialLevelTemplate>())
                UnityEngine.Object.DestroyImmediate(marker);

            AttractionTemplate attraction = root.GetComponent<AttractionTemplate>()
                ?? root.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = new Vector2(DreamSequenceTemplate.StandardWidthFeet,
                DreamSequenceTemplate.StandardLengthFeet);
            if (setStarterDefaults)
            {
                attraction.generateFloor = false;
                attraction.generateCeiling = false;
                attraction.gameRequiresAttraction = false;
            }
        }

        private static void AllowEmptySpecialLevelShrink(AttractionTemplate level)
        {
            // A blank Game Over level has no blockers. The generic baker uses its
            // authored footprint for an empty attraction, which would pin every
            // Sequence to 12 × 18 ft just because of this empty screen.
            AttractionPackingBake bake = level.PackingBake;
            if (bake == null || bake.Props.Count != 0) return;
            Vector2 authored = bake.AuthoredFootprintMeters;
            Vector2 minimum = new Vector2(0.1f, 0.1f);
            float ratioX = minimum.x / authored.x;
            float ratioZ = minimum.y / authored.y;
            var followers = new List<AttractionPackingFollowerPose>();
            foreach (AttractionPackingFollowerPose pose in bake.Followers)
            {
                if (pose == null) continue;
                Vector3 authoredPosition = pose.AuthoredPosition;
                Vector3 shrinkPosition = new Vector3(
                    authoredPosition.x * ratioX, authoredPosition.y,
                    authoredPosition.z * ratioZ);
                Vector3 scale = pose.AuthoredLocalScale;
                Vector3 shrinkScale;
                if (pose.ScaleMode == AttractionPackingScaleMode.XYZUniform)
                    shrinkScale = scale * Mathf.Min(ratioX, ratioZ);
                else if (pose.ScaleMode == AttractionPackingScaleMode.XZUniform)
                {
                    float ratio = Mathf.Min(ratioX, ratioZ);
                    shrinkScale = new Vector3(scale.x * ratio, scale.y, scale.z * ratio);
                }
                else shrinkScale = new Vector3(scale.x * ratioX, scale.y, scale.z * ratioZ);
                followers.Add(new AttractionPackingFollowerPose(
                    pose.Follower, authoredPosition, shrinkPosition,
                    pose.GrownPosition, shrinkPosition, scale, shrinkScale,
                    pose.GrownLocalScale, shrinkScale, pose.ScaleMode));
            }
            level.SetPackingBake(new AttractionPackingBake(
                authored, bake.SafeFootprintMeters, minimum,
                bake.GrowFootprintMeters, minimum,
                new List<AttractionPropPackingPose>(), followers));
        }

        private static Vector2 ExpandMinimum(Vector2 current, AttractionTemplate level, bool allowRotation)
        {
            if (level == null || !level.HasPackingBake)
                throw new InvalidOperationException(
                    $"Sequence level '{level?.name ?? "missing"}' is missing a valid packing bake.");
            Vector2 minimum = level.PackingBake.ShrinkFootprintMeters;
            if (allowRotation && DreamSequenceCompatibility.ShouldRotate(level))
                minimum = new Vector2(minimum.y, minimum.x);
            return new Vector2(Mathf.Max(current.x, minimum.x),
                Mathf.Max(current.y, minimum.y));
        }

        private static bool NeedsLevelRefresh(string path, DreamSequenceSpecialLevelRole role)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) return true;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(BorderPrefabPath) != null
                && !HasBorderScaffoldStamp(path)) return true;
            if (FindChildRecursive(asset.transform, "Sequence Floor Border") != null
                && !HasScaffoldStamp(path, FollowerScaffoldStamp)) return true;
            if (role == DreamSequenceSpecialLevelRole.Start
                && !HasScaffoldStamp(path, StartControlScaffoldStamp)) return true;
            if (role == DreamSequenceSpecialLevelRole.Overlay
                && (!HasScaffoldStamp(path, NavigationScaffoldStamp)
                    || !HasScaffoldStamp(path, TransitionScaffoldStamp))) return true;
            if (role == DreamSequenceSpecialLevelRole.GameOver
                && !HasScaffoldStamp(path, EmptyGameOverShrinkStamp)) return true;
            if (!HasScaffoldStamp(path, LevelDefaultsScaffoldStamp)) return true;
            AttractionTemplate attraction = asset.GetComponent<AttractionTemplate>();
            if (role == DreamSequenceSpecialLevelRole.GameOver && attraction != null
                && attraction.HasPackingBake && attraction.PackingBake.Props.Count == 0
                && attraction.PackingBake.ShrinkFootprintMeters.x > 0.2f) return true;
            if (attraction == null || attraction.size != GameLevelSize.Custom
                || attraction.customSize != new Vector2(DreamSequenceTemplate.StandardWidthFeet,
                    DreamSequenceTemplate.StandardLengthFeet)
                || asset.GetComponent<DreamSequenceSpecialLevelTemplate>() != null
                || FindChildRecursive(asset.transform, "12 ft × 18 ft Level Floor") != null
                || FindChildRecursive(asset.transform, "LevelFloor") != null
                || FindChildRecursive(asset.transform, "Game Over Backdrop") != null)
                return true;

            return false;
        }

        private static bool IsLegacyNavigation(Transform navigation)
        {
            if (navigation == null) return false;
            Transform backplate = FindChildRecursive(navigation, "Navigation Backplate");
            return navigation.localPosition.x > 0f
                || backplate == null
                || Mathf.Abs(backplate.localScale.x - 0.58f) > 0.001f
                || Mathf.Abs(backplate.localScale.y - 1.15f) > 0.001f;
        }

        private static bool NeedsNavigationRefresh(Transform navigation)
        {
            GameObject reusable = AssetDatabase.LoadAssetAtPath<GameObject>(
                ElevatorControlsPrefabPath);
            // Developers can freely reshape and rename controls in the shared
            // prefab. Only migrate a level when its control is not an instance
            // of that prefab; inspecting its children would erase their edits.
            return reusable != null
                ? !IsPrefabInstanceOf(navigation, reusable)
                : IsLegacyNavigation(navigation);
        }

        private static bool NeedsNavigationFacingRefresh(Transform navigation)
        {
            // The prefab's buttons face local -Z. The old +90° default pointed
            // away from the room when placed at its negative-X edge.
            return navigation != null && navigation.localPosition.x < 0f
                && Quaternion.Angle(navigation.localRotation,
                    Quaternion.Euler(0f, 90f, 0f)) < 0.5f;
        }

        private static Transform FindStartButton(Transform root)
        {
            Transform button = FindChildRecursive(root, "START — Super Adventure Land Button");
            return button ?? FindChildRecursive(root, "START — Fallback Button");
        }

        private static void RemoveChild(Transform root, string name)
        {
            Transform child;
            while ((child = FindChildRecursive(root, name)) != null && child != root)
                UnityEngine.Object.DestroyImmediate(child.gameObject);
        }

        private static GameObject InstantiateSpecialLevel(string path, Transform parent, string name)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            GameObject instance = prefab != null ? PrefabUtility.InstantiatePrefab(prefab) as GameObject : null;
            if (instance == null) return null;
            instance.name = name;
            instance.transform.SetParent(parent, false);
            return instance;
        }

        private static void CreateExtractedStartButton(Transform parent, Material fallbackMaterial,
            TextAsset buttonScript)
        {
            GameObject reusable = AssetDatabase.LoadAssetAtPath<GameObject>(
                StartButtonPrefabPath);
            if (reusable != null)
            {
                GameObject instance = PrefabUtility.InstantiatePrefab(reusable, parent) as GameObject;
                if (instance != null)
                {
                    instance.name = "START — Super Adventure Land Button";
                    instance.transform.localPosition = Vector3.zero;
                }
                return;
            }

            Mesh extractedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(StartButtonMeshPath);
            GameObject button;
            if (extractedMesh != null)
            {
                button = new GameObject("START — Super Adventure Land Button");
                button.name = "START — Super Adventure Land Button";
                button.transform.SetParent(parent, false);
                // The mesh pivot is at its bottom, so y=0 places the control
                // directly on the level ground as a player-sized step plate.
                button.transform.localPosition = Vector3.zero;
                // Preserve the authored presentation from SAL's A_DreamSequence.
                // The imported FBX sub-object itself is unscaled, while the scene
                // instance supplies the squat, wide button silhouette.
                button.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                button.transform.localScale = new Vector3(0.6772702f, 0.4715968f, 0.6772702f);
                button.AddComponent<MeshFilter>().sharedMesh = extractedMesh;
                Material startMaterial = AssetDatabase.LoadAssetAtPath<Material>(StartButtonMaterialPath);
                button.AddComponent<MeshRenderer>().sharedMaterial = startMaterial != null
                    ? startMaterial : fallbackMaterial;
                var trigger = button.AddComponent<BoxCollider>();
                trigger.center = extractedMesh.bounds.center;
                trigger.size = extractedMesh.bounds.size;
                trigger.isTrigger = true;
            }
            else
            {
                button = CreateCube("START — Fallback Button", parent, new Vector3(0f, 0.15f, 0f),
                    new Vector3(1.8f, 0.3f, 1.8f), fallbackMaterial, true);
            }
            AddButtonScript(button, buttonScript, "start");
            DreamSequenceControlPrefabBuilder.EnsureButtonFeedback(button);
        }

        private static bool UsesReusableStartButton(Transform button)
        {
            GameObject reusable = AssetDatabase.LoadAssetAtPath<GameObject>(
                StartButtonPrefabPath);
            if (reusable != null) return IsPrefabInstanceOf(button, reusable);
            Mesh expected = AssetDatabase.LoadAssetAtPath<Mesh>(StartButtonMeshPath);
            MeshFilter filter = button != null ? button.GetComponent<MeshFilter>() : null;
            return expected != null && filter != null && filter.sharedMesh == expected;
        }

        private static bool IsPrefabInstanceOf(Transform instance, GameObject prefab)
        {
            if (instance == null || prefab == null) return false;
            GameObject source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(instance.gameObject);
            if (source == null) source = PrefabUtility.GetCorrespondingObjectFromSource(instance.gameObject);
            return source == prefab
                || string.Equals(AssetDatabase.GetAssetPath(source), AssetDatabase.GetAssetPath(prefab),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static Vector3 StartButtonGroundPosition(Transform button)
        {
            // The extracted SAL mesh has a bottom pivot. Unity's fallback cube
            // is center-pivoted, so lift it by half of its 0.3 m height.
            return button != null && button.name == "START — Fallback Button"
                ? new Vector3(0f, 0.15f, 0f)
                : Vector3.zero;
        }

        private static void CreateNavigation(Transform root, Material material, Material arrowMaterial,
            TextAsset buttonScript)
        {
            GameObject reusable = AssetDatabase.LoadAssetAtPath<GameObject>(
                ElevatorControlsPrefabPath);
            if (reusable != null)
            {
                GameObject instance = PrefabUtility.InstantiatePrefab(reusable, root) as GameObject;
                if (instance != null)
                {
                    instance.name = "Default 3D Level Navigation";
                    // The reusable control now includes a standing base. Its
                    // authored buttons are already at human height above y=0.
                    instance.transform.localPosition = new Vector3(-1.72f, 0f, 0f);
                    instance.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
                }
                return;
            }

            var nav = new GameObject("Default 3D Level Navigation");
            nav.transform.SetParent(root, false);
            // Human-scale elevator controls on the left wall. The Overlay stays
            // active while levels swap below it.
            nav.transform.localPosition = new Vector3(-1.72f, 1.25f, 0f);
            nav.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
            CreateCube("Navigation Backplate", nav.transform, Vector3.zero,
                new Vector3(0.58f, 1.15f, 0.10f), material, false);
            CreateElevatorButton("UP", nav.transform, new Vector3(0f, 0.27f, -0.09f),
                false, arrowMaterial, buttonScript, "back");
            CreateElevatorButton("DOWN", nav.transform, new Vector3(0f, -0.27f, -0.09f),
                true, arrowMaterial, buttonScript, "forward");
        }

        private static void CreateTransitionEffect(Transform root)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TransitionEffectPrefabPath);
            if (prefab == null) return;
            GameObject instance = PrefabUtility.InstantiatePrefab(prefab, root) as GameObject;
            if (instance != null) instance.name = "Default Level Transition";
        }

        private static Transform CreateDefaultBorder(Transform root)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BorderPrefabPath);
            if (prefab == null) throw new InvalidOperationException("Sequence border prefab is missing.");
            GameObject instance = PrefabUtility.InstantiatePrefab(prefab, root) as GameObject;
            if (instance == null) throw new InvalidOperationException("Could not instantiate the Sequence border.");
            return instance.transform;
        }

        private static void RecalculateBorderBounds(Transform border)
        {
            FloorAnchor anchor = border.GetComponent<FloorAnchor>();
            if (anchor == null) throw new InvalidOperationException("Sequence border is missing its FloorAnchor.");
            anchor.PrecalculateBounds();
            if (!anchor.HasPrecalculatedBounds())
                throw new InvalidOperationException("Could not calculate Sequence border bounds.");
            PrefabUtility.RecordPrefabInstancePropertyModifications(anchor);
        }

        private static void EnsureBorderFollower(Transform border)
        {
            AttractionPackingFollower follower = border.GetComponent<AttractionPackingFollower>()
                ?? border.gameObject.AddComponent<AttractionPackingFollower>();
            follower.enabled = true;
            follower.followPacking = true;
            follower.scaleMode = AttractionPackingScaleMode.XZIndependent;
            follower.avoidNewPropOverlaps = false;
            PrefabUtility.RecordPrefabInstancePropertyModifications(follower);
        }

        private static bool HasBorderScaffoldStamp(string path)
        {
            return HasScaffoldStamp(path, BorderScaffoldStamp);
        }

        private static void StampBorderScaffold(string path)
        {
            StampScaffold(path, BorderScaffoldStamp);
        }

        private static bool HasScaffoldStamp(string path, string stamp)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            return importer != null && (importer.userData ?? string.Empty).Contains(stamp);
        }

        private static void StampScaffold(string path, string stamp)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer == null || HasScaffoldStamp(path, stamp)) return;
            importer.userData = string.IsNullOrEmpty(importer.userData)
                ? stamp : importer.userData + "\n" + stamp;
            importer.SaveAndReimport();
        }

        private static void CreateElevatorButton(string name, Transform parent, Vector3 position,
            bool pointsDown, Material arrowMaterial, TextAsset buttonScript, string action)
        {
            GameObject button = CreateCube(name, parent, position,
                new Vector3(0.38f, 0.38f, 0.12f), arrowMaterial, true);
            button.transform.localRotation = Quaternion.Euler(0f, 0f, pointsDown ? 180f : 0f);
            AddButtonScript(button, buttonScript, action);
            DreamSequenceControlPrefabBuilder.EnsureButtonFeedback(button);
        }

        private static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static void RemoveDirectChild(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            if (child != null) UnityEngine.Object.DestroyImmediate(child.gameObject);
        }

        private static GameObject FindDirectPrefabInstance(Transform parent, GameObject prefab)
        {
            if (parent == null || prefab == null) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                GameObject child = parent.GetChild(i).gameObject;
                GameObject source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(child);
                if (source == prefab) return child;
            }
            return null;
        }

        private static GameObject CreateCube(string name, Transform parent, Vector3 position, Vector3 scale, Material material, bool trigger)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.GetComponent<BoxCollider>().isTrigger = trigger;
            return go;
        }

        private static void AddButtonScript(GameObject go, TextAsset script, string action)
        {
            var lua = go.AddComponent<LuaBehaviour>();
            lua.luaScript = script;
            lua.stringInjections = new[] { new StringInjection { name = "action", value = action } };
        }

        private static Material GetOrCreateMaterial(string path)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            Shader shader = Shader.Find("Shader Graphs/DreamPark-Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            var material = new Material(shader) { name = "Dream Sequence Default" };
            if (material.HasProperty("_baseColor")) material.SetColor("_baseColor", new Color(0.15f, 0.55f, 1f, 1f));
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static Material GetOrCreateArrowMaterial(string path)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Shader Graphs/DreamPark-Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                material = new Material(shader) { name = "Dream Sequence Elevator Arrow" };
                AssetDatabase.CreateAsset(material, path);
            }

            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(ElevatorArrowTexturePath);
            if (material.HasProperty("_baseTex")) material.SetTexture("_baseTex", texture);
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_baseColor")) material.SetColor("_baseColor", Color.white);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static string EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, child);
            return path;
        }

        private static void WriteStarterTextAsset(string path, string contents)
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            // These are creator scripts, not generated build output. In particular,
            // a refresh must never replace the title's game rules with our defaults.
            if (File.Exists(full)) return;
            File.WriteAllText(full, contents);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        private static string Sanitize(string value)
        {
            string result = new string((value ?? "DreamSequence").Where(char.IsLetterOrDigit).ToArray());
            return string.IsNullOrEmpty(result) ? "DreamSequence" : result;
        }

        private const string ControllerLua = @"-- Generated Dream Sequence controller. Safe to customize.
-- @var transitionDelay float 2.5 (minimum; matches Super Adventure Land)
local UE = CS.UnityEngine
local ParticleSystemType = typeof(UE.ParticleSystem)
local levels = {}
local current = 1
local running = false
local pendingLevel = nil
local revealAt = nil
local stopEffectAt = nil

local function rebuild()
    levels = {}
    local t = levelParent.transform
    for i = 0, t.childCount - 1 do levels[#levels + 1] = t:GetChild(i).gameObject end
end

local function show(index)
    if #levels == 0 then return end
    current = math.max(1, math.min(index, #levels))
    for i = 1, #levels do levels[i]:SetActive(i == current) end
    if current == #levels then running = false end
end

local function transition_to(index)
    if #levels == 0 or pendingLevel ~= nil or stopEffectAt ~= nil then return end
    local destination = math.max(1, math.min(index, #levels))
    if destination == current then return end
    if transitionEffect == nil then show(destination); return end
    transitionEffect:SetActive(true)
    local particles = transitionEffect:GetComponentsInChildren(ParticleSystemType, true)
    for i = 0, particles.Length - 1 do particles[i]:Play(true) end
    pendingLevel = destination
    -- Give players time to anticipate the next level and keep particles over
    -- the activation frame, which can otherwise expose a loading hitch.
    local duration = math.max(2.5, transitionDelay or 2.5)
    revealAt = UE.Time.time + duration
    stopEffectAt = revealAt
end

function start_game() running = true; transition_to(math.min(2, #levels)) end
function advance() transition_to(current + 1) end
function back() transition_to(current - 1) end
function is_running() return running end
function update()
    local now = UE.Time.time
    if pendingLevel ~= nil and now >= revealAt then
        show(pendingLevel)
        pendingLevel = nil
        revealAt = nil
    end
    if stopEffectAt ~= nil and now >= stopEffectAt then
        local particles = transitionEffect:GetComponentsInChildren(ParticleSystemType, true)
        for i = 0, particles.Length - 1 do
            particles[i]:Stop(true, UE.ParticleSystemStopBehavior.StopEmitting)
        end
        stopEffectAt = nil
    end
end
function onready()
    rebuild(); show(autoStart == true and math.min(2, #levels) or 1)
    if globalName ~= nil and globalName ~= '' then rawset(_G, globalName, self.ScriptScope) end
end
function ondestroy() if globalName ~= nil and globalName ~= '' then rawset(_G, globalName, nil) end end
";

        private const string ContainerLua = @"-- This is your game's persistent Game Manager. DreamPark creates it once.
-- Edit or replace these functions to own routing, score, coins, achievements,
-- randomizers, hub worlds, or anything else that should survive level changes.
-- dp.load_level(index) requests an asynchronous platform load. Use this
-- manager's load_level(index[, groupId]) for shared gameplay navigation.
-- Index 1 is Start; subsequent indices are the package's ordered attractions;
-- the last index is Game Over. Group indices start at 0. Your routing does
-- not have to be linear: next_level can call load_level(0, 'your-group-id').
-- The Container has one NetId. Navigation is shared by every connected player.
local slot = 1
local revision = 0
local writer = ''
local pendingSlot, pendingRevision, pendingWriter, pendingLocal = nil, nil, nil, false
local receivedRemoteState = false
local relayWasConnected = false
local nextSyncAt = 0

local function newer(r, u, oldRevision, oldWriter)
    return r > oldRevision or (r == oldRevision and u > oldWriter)
end

local function clear_pending()
    pendingSlot, pendingRevision, pendingWriter, pendingLocal = nil, nil, nil, false
end

local function publish_state()
    if net_send ~= nil then
        net_send('sequence_state', {slot = slot, revision = revision, writer = writer})
    end
end

local function on_level_loaded(index)
    if pendingSlot == index then
        slot, revision, writer = index, pendingRevision, pendingWriter
        local publish = pendingLocal
        clear_pending()
        if publish then publish_state() end
    elseif pendingSlot ~= nil or index ~= slot then
        -- A creator may still use the low-level dp.load_level directly,
        -- including while a manager request is pending. Unity cancels the
        -- older request when the new one wins.
        local nextRevision = math.max(revision, pendingRevision or revision) + 1
        clear_pending()
        slot, revision, writer = index, nextRevision, tostring(dp.me() or '')
        publish_state()
    end
end

local function on_level_failed(index)
    local superseded = pendingSlot ~= nil and pendingSlot ~= index
    if pendingSlot ~= index and not superseded then return end
    local wasRemote = not pendingLocal or superseded
    clear_pending()
    if wasRemote then
        receivedRemoteState = false
        nextSyncAt = 0
    end
    print('[DreamSequence] Level ' .. tostring(index) .. ' failed: ' .. tostring(dp.level_error()))
end

local function request_level(index, r, u, localRequest)
    if type(index) ~= 'number' or index < 1 or index > dp.level_count() then return false end
    if not newer(r, u, revision, writer) then return false end
    if pendingRevision ~= nil and not newer(r, u, pendingRevision, pendingWriter) then return false end
    pendingSlot, pendingRevision, pendingWriter, pendingLocal = index, r, u, localRequest
    if not dp.load_level(index) then
        clear_pending()
        return false
    end
    return true
end

-- Public creator policy entry point. Absolute slots remain 1-based. Passing
-- a Group ID makes the index zero-based within that authored Group.
function load_level(index, groupId)
    if pendingSlot ~= nil then return false end
    if groupId ~= nil and groupId ~= '' then index = dp.level_slot(index, groupId) end
    local mine = tostring(dp.me() or '')
    return request_level(index, revision + 1, mine, true)
end

function onmessage(kind, p)
    if p == nil then return end
    if kind == 'sequence_sync_request' then
        publish_state()
    elseif kind == 'sequence_state' and type(p.revision) == 'number'
        and type(p.writer) == 'string' and type(p.slot) == 'number'
        and p.slot >= 1 and p.slot <= dp.level_count() then
        receivedRemoteState = true
        request_level(p.slot, p.revision, p.writer, false)
    end
end

function awake()
    slot = math.max(1, dp.current_level_index())
    dp.on_level_loaded(on_level_loaded)
    dp.on_level_failed(on_level_failed)
end

function ondestroy()
    dp.off_level_loaded(on_level_loaded)
    dp.off_level_failed(on_level_failed)
end

function update()
    if net_send == nil then return end
    local connected = dp.relay().connected
    if connected and (not relayWasConnected or
        (not receivedRemoteState and CS.UnityEngine.Time.time >= nextSyncAt)) then
        net_send('sequence_sync_request', {})
        nextSyncAt = CS.UnityEngine.Time.time + 2
    end
    if not connected then receivedRemoteState = false end
    relayWasConnected = connected
end

function start_game()
    return load_level(2)
end

function next_level()
    return load_level(slot + 1)
end

function previous_level()
    return load_level(slot - 1)
end
";

        private const string ButtonLua = @"-- Generated hand-collider navigation button. Safe to customize.
local armed = true
function ontriggerenter(other)
    if not armed or not dp.is_player(other) or not dp.button_press() then return end
    armed = false
    local c = dp.game_manager()
    if action == 'start' and c ~= nil and c.start_game ~= nil then c.start_game()
    elseif action == 'back' and c ~= nil and c.previous_level ~= nil then c.previous_level()
    elseif action == 'back' then dp.previous_level()
    elseif c ~= nil and c.next_level ~= nil then c.next_level()
    else dp.next_level() end
end
function ontriggerexit(other) if dp.is_player(other) then armed = true end end
";
    }
}
#endif
