// SDK-only: game projects build addressable content, and this keeps Unity
// from rebuilding it on every Player build. CORE has no addressables build
// at all (content is remote-only), so here this script did nothing but log
// a warning on every domain reload — gated out like the rest of the
// content-processing pipeline.
#if UNITY_EDITOR && !DREAMPARKCORE
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

[InitializeOnLoad]
public static class DisableAddressablesAutoBuild
{
    static DisableAddressablesAutoBuild()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings != null)
        {
            settings.BuildAddressablesWithPlayerBuild = 
                AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            Debug.Log("✅ Addressables auto-build disabled. Using existing bundles only.");
        }
        else
        {
            Debug.LogWarning("⚠️ No AddressableAssetSettings found — could not disable auto-build.");
        }
    }
}
#endif