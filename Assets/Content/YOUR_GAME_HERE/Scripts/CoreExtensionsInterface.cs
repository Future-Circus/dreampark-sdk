namespace YOUR_GAME_HERE
{
    using System;
    using UnityEngine;

    public static class CoreExtensionsInterface
    {
        public static string gameId = "YOUR_GAME_HERE";
        public static void GetAsset<T>(this string resourceName, Action<T> onSuccess = null, Action<string> onError = null) where T : UnityEngine.Object
        {
            var assetType = typeof(T);
            string typeFolder = null;
            if (assetType != typeof(GameObject))
            {
                if (assetType == typeof(AudioClip))
                    typeFolder = "Audio";
                else if (assetType == typeof(Texture) || assetType == typeof(Texture2D) || assetType == typeof(RenderTexture))
                    typeFolder = "Textures";
                else if (assetType == typeof(Material))
                    typeFolder = "Materials";
                else if (assetType == typeof(Shader))
                    typeFolder = "Shaders";
                else if (assetType == typeof(AnimationClip))
                    typeFolder = "Animations";
                else if (assetType == typeof(TextAsset))
                    typeFolder = "Data";
            }

            string desiredAddress = resourceName;
            if (assetType == typeof(GameObject))
                desiredAddress = $"{gameId}/{resourceName}";
            else if (!string.IsNullOrEmpty(typeFolder))
                desiredAddress = $"{gameId}/{typeFolder}/{resourceName}";

            CoreExtensions.GetAssetAsync<T>(desiredAddress, onSuccess, onError).Forget();
        }

        public static void PlaySFX(this string clipName, Vector3 position, float volume = 1f, float pitch = 1f, Transform parent = null)
        {
            CoreExtensions.GetAssetAsync<AudioClip>($"{gameId}/Audio/{clipName}", (clip) => {
                if (clip)
                {
                clip.PlaySFX(position, volume, pitch, parent);
                }
            }, null).Forget();
        }

        public static void SpawnAsset(this string resourceName, Action<GameObject> onSuccess, Action<string> onError)
        {
            CoreExtensions.SpawnAsset($"{gameId}/{resourceName}", onSuccess, onError);
        }

        public static void SpawnAsset(this string resourceName, Vector3 position, Quaternion rotation, Transform parent = null, Action<GameObject> onSuccess = null, Action<string> onError = null)
        {
            CoreExtensions.SpawnAsset($"{gameId}/{resourceName}", position, rotation, parent, onSuccess, onError);
        }
    }
}
