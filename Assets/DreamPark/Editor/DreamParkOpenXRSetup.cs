#if UNITY_EDITOR && DREAMPARK_SDK_PACKAGES_READY
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace DreamPark.Editor
{
    /// <summary>
    /// Keeps DreamPark's Quest projects on the supported OpenXR feature sets.
    /// Package upgrades can recreate OpenXR settings assets with every feature disabled,
    /// so this setup is intentionally idempotent and also runs before Android builds.
    /// </summary>
    public sealed class DreamParkOpenXRSetup : IPreprocessBuildWithReport
    {
        private const string MetaQuestFeatureSetId = "com.unity.openxr.featureset.meta";
        private const string MetaXRFeatureSetId = "com.meta.openxr.featureset.metaxr";
        private const string OpenXRLoaderPath = "Assets/XR/Loaders/OpenXRLoader.asset";
        private const string MetaDevAgentSettingsPath = "Assets/Resources/DevAgentSettings.asset";

        private static readonly BuildTargetGroup[] SupportedGroups =
        {
            BuildTargetGroup.Android,
            BuildTargetGroup.Standalone,
        };

        // Feature-set defaults cover the AR Foundation providers. These explicit features
        // preserve DreamPark's existing controller, hand, depth, and Quest performance setup.
        private static readonly HashSet<string> AndroidRequiredFeatureIds = new(StringComparer.Ordinal)
        {
            "com.unity.openxr.feature.metaquest",
            "com.meta.openxr.feature.metaxr",
            "com.unity.openxr.feature.compositionlayers",
            "com.unity.openxr.feature.arfoundation-meta-occlusion",
            "com.unity.openxr.feature.input.oculustouch",
            "com.unity.openxr.feature.input.handinteraction",
            "com.unity.openxr.feature.input.handinteractionposes",
            "com.meta.openxr.feature.foveation",
            "com.meta.openxr.feature.subsampledLayout",
        };

        private static readonly HashSet<string> StandaloneRequiredFeatureIds = new(StringComparer.Ordinal)
        {
            "com.meta.openxr.feature.metaxr",
            "com.unity.openxr.feature.input.oculustouch",
            "com.meta.openxr.feature.foveation",
        };

        public int callbackOrder => -1000;

        [InitializeOnLoadMethod]
        private static void ScheduleConfigurationCheck()
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    ApplyConfiguration(logSuccess: false);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"DreamPark OpenXR setup failed: {exception.Message}");
                }
            };
        }

        [MenuItem("DreamPark/Configuration/Apply Required OpenXR Settings")]
        public static void ApplyFromMenu()
        {
            ApplyConfiguration(logSuccess: true);
        }

        // Entry point for CI and Unity CLI:
        // unity -batchmode -executeMethod DreamPark.Editor.DreamParkOpenXRSetup.ApplyBatch
        public static void ApplyBatch()
        {
            ApplyConfiguration(logSuccess: true);
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
                return;

            try
            {
                ApplyConfiguration(logSuccess: false);
            }
            catch (Exception exception)
            {
                throw new BuildFailedException($"DreamPark OpenXR setup is incomplete: {exception.Message}");
            }
        }

        private static void ApplyConfiguration(bool logSuccess)
        {
            OpenXRFeatureSetManager.InitializeFeatureSets();

            var changed = EnsureQuestPlayerSettings();
            changed |= ScrubMetaDevAgentSettings();
            foreach (var group in SupportedGroups)
            {
                changed |= EnsureOpenXRLoader(group);
                changed |= EnableFeatureSet(group, MetaQuestFeatureSetId);
                changed |= EnableFeatureSet(group, MetaXRFeatureSetId);
                OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(group);
                changed |= EnableRequiredFeatures(group);
                Validate(group);
            }

            if (changed)
                AssetDatabase.SaveAssets();

            if (logSuccess)
                Debug.Log("DreamPark OpenXR configuration is ready for Android and Standalone.");
        }

        private static bool EnsureQuestPlayerSettings()
        {
            var changed = false;

            // Unity's OpenXR validation runs before the project's legacy orientation
            // processor. Persist the Quest requirement so validation and CLI builds see it.
            if (PlayerSettings.defaultInterfaceOrientation != UIOrientation.LandscapeLeft)
            {
                PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
                changed = true;
            }

            if (PlayerSettings.Android.applicationEntry != AndroidApplicationEntry.GameActivity)
            {
                PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
                changed = true;
            }

            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings != null &&
                settings.latencyOptimization != OpenXRSettings.LatencyOptimization.PrioritizeInputPolling)
            {
                settings.latencyOptimization = OpenXRSettings.LatencyOptimization.PrioritizeInputPolling;
                EditorUtility.SetDirty(settings);
                changed = true;
            }

            return changed;
        }

        internal static bool ScrubMetaDevAgentSettings()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(MetaDevAgentSettingsPath);
            if (asset == null)
                return false;

            var serialized = new SerializedObject(asset);
            var changed = SetBool(serialized, "enabled", false);
            changed |= SetString(serialized, "serverAddress", string.Empty);
            changed |= SetString(serialized, "accessToken", string.Empty);
            changed |= SetString(serialized, "witClientAccessToken", string.Empty);
            if (!changed)
                return false;

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            Debug.Log("DreamPark disabled and scrubbed Meta DevAgent bridge settings; Unity CLI/Pipeline remains the project automation path.");
            return true;
        }

        private static bool SetBool(SerializedObject serialized, string propertyName, bool value)
        {
            var property = serialized.FindProperty(propertyName);
            if (property == null || property.boolValue == value)
                return false;

            property.boolValue = value;
            return true;
        }

        private static bool SetString(SerializedObject serialized, string propertyName, string value)
        {
            var property = serialized.FindProperty(propertyName);
            if (property == null || property.stringValue == value)
                return false;

            property.stringValue = value;
            return true;
        }

        private static bool EnsureOpenXRLoader(BuildTargetGroup group)
        {
            var generalSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
            var manager = generalSettings != null ? generalSettings.Manager : null;
            if (manager == null)
                throw new InvalidOperationException($"XR Manager settings are missing for {group}.");

            if (manager.activeLoaders.Any(loader => loader is OpenXRLoader))
                return false;

            var loaderAsset = AssetDatabase.LoadAssetAtPath<OpenXRLoader>(OpenXRLoaderPath);
            if (loaderAsset == null)
                throw new InvalidOperationException($"OpenXR loader asset is missing at {OpenXRLoaderPath}.");

            if (!manager.TryAddLoader(loaderAsset, 0))
                throw new InvalidOperationException($"Could not assign the OpenXR loader to {group}.");

            EditorUtility.SetDirty(manager);
            return true;
        }

        private static bool EnableFeatureSet(BuildTargetGroup group, string featureSetId)
        {
            var featureSet = OpenXRFeatureSetManager.GetFeatureSetWithId(group, featureSetId);
            if (featureSet == null || !featureSet.isInstalled)
                throw new InvalidOperationException($"OpenXR feature set '{featureSetId}' is unavailable for {group}.");

            if (featureSet.isEnabled)
                return false;

            featureSet.isEnabled = true;
            return true;
        }

        private static bool EnableRequiredFeatures(BuildTargetGroup group)
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null)
                throw new InvalidOperationException($"OpenXR settings are missing for {group}.");

            var requiredFeatureIds = GetRequiredFeatureIds(group);
            var changed = false;
            foreach (var feature in settings.GetFeatures<OpenXRFeature>())
            {
                var featureId = GetFeatureId(feature);
                if (!requiredFeatureIds.Contains(featureId) || feature.enabled)
                    continue;

                feature.enabled = true;
                EditorUtility.SetDirty(feature);
                changed = true;
            }

            if (changed)
                EditorUtility.SetDirty(settings);

            return changed;
        }

        private static void Validate(BuildTargetGroup group)
        {
            var generalSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
            if (generalSettings?.Manager == null ||
                !generalSettings.Manager.activeLoaders.Any(loader => loader is OpenXRLoader))
            {
                throw new InvalidOperationException($"OpenXR is not the active XR loader for {group}.");
            }

            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null)
                throw new InvalidOperationException($"OpenXR settings are missing for {group}.");

            var enabledIds = settings.GetFeatures<OpenXRFeature>()
                .Where(feature => feature.enabled)
                .Select(GetFeatureId)
                .Where(featureId => !string.IsNullOrEmpty(featureId))
                .ToHashSet(StringComparer.Ordinal);

            var missingIds = GetRequiredFeatureIds(group)
                .Where(featureId => !enabledIds.Contains(featureId))
                .ToArray();
            if (missingIds.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{group} is missing required OpenXR features: {string.Join(", ", missingIds)}.");
            }
        }

        private static HashSet<string> GetRequiredFeatureIds(BuildTargetGroup group)
        {
            return group == BuildTargetGroup.Android
                ? AndroidRequiredFeatureIds
                : StandaloneRequiredFeatureIds;
        }

        private static string GetFeatureId(OpenXRFeature feature)
        {
            return feature.GetType()
                .GetCustomAttributes(typeof(OpenXRFeatureAttribute), inherit: true)
                .OfType<OpenXRFeatureAttribute>()
                .FirstOrDefault()
                ?.FeatureId;
        }
    }

    /// <summary>
    /// Meta XR 205 creates DevAgent settings even when its Agent Bridge toggle is off,
    /// and its build callback injects a machine-local address and token unconditionally.
    /// Run after package preprocessors and postprocessors so those values never ship or
    /// remain in the project. DreamPark automation uses Unity CLI and Unity Pipeline.
    /// </summary>
    public sealed class DreamParkMetaDevAgentBuildGuard : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        public void OnPreprocessBuild(BuildReport report)
        {
            DreamParkOpenXRSetup.ScrubMetaDevAgentSettings();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            DreamParkOpenXRSetup.ScrubMetaDevAgentSettings();
        }
    }
}
#endif
