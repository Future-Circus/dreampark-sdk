#if UNITY_EDITOR
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