using System;
using System.Collections;
using Defective.JSON;
using Meta.XR;
using UnityEngine;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

#if UNITY_EDITOR
using Unity.EditorCoroutines.Editor;
#endif

namespace DreamPark.API
{
    /// <summary>
    /// One-call screenshot capture + upload, modeled on Xbox's "press a button,
    /// it shows up on your profile" flow. Capture is composited locally
    /// (passthrough camera feed + virtual content) into a Texture2D, then
    /// shipped through the two-step presigned URL flow against
    /// /api/user/screenshots so the bytes never touch our app server.
    ///
    /// IMPORTANT — Quest 3S MR composite caveat:
    /// On Quest 3 / 3S, the OXR compositor draws passthrough as a layer
    /// AFTER the app submits the eye buffer. That means a naive
    /// ScreenCapture.CaptureScreenshotAsTexture() returns ONLY the virtual
    /// content (transparent where passthrough would show). To get a true
    /// composite we read the passthrough texture via the Passthrough
    /// Camera API (v76+) and blit it under the virtuals ourselves.
    ///
    /// Capture flow:
    ///   1) Find the active PassthroughCameraAccess (cached after first call).
    ///   2) Snap the camera feed texture into a RT (background layer).
    ///   3) Repoint the main Camera at a transparent RT, re-render, blit the
    ///      virtuals on top of the background RT.
    ///   4) ReadPixels -> Texture2D -> EncodeToPNG / EncodeToJPG.
    ///
    /// Upload flow (matches api.testContent.routes.js):
    ///   1) POST /api/user/screenshots/uploadUrl  → { uploadUrl, screenshotId }
    ///   2) PUT bytes directly to GCS
    ///   3) POST /api/user/screenshots/commit     → publishes to profile
    /// </summary>
    public static class ScreenshotAPI
    {
        /// <summary>
        /// Fires when an upload completes (success/fail). UX layers can hook
        /// this to flash a "Saved to profile" toast à la Xbox capture.
        /// </summary>
        public static event Action<bool, string, string> ScreenshotUploaded; // (success, screenshotId, mediaUrl)

        // JPEG > PNG for screenshots: ~5-10x smaller for the same visual
        // quality at 90, faster to encode, faster to upload over park Wi-Fi.
        // PNG capture is still available for callers that want lossless.
        private const int DefaultJpegQuality = 90;

        private static PassthroughCameraAccess _cachedCameraAccess;

        // --- Public entry points -----------------------------------------

        /// <summary>
        /// Xbox-style one-call capture + upload. Pulls parkId and contentIds
        /// from SessionContext. Caller doesn't have to wait — fire-and-forget
        /// is fine, hook ScreenshotUploaded for completion.
        /// </summary>
        public static void CaptureAndUpload(Action<bool, string> onComplete = null)
        {
            CaptureAndUpload(
                parkId: SessionContext.LocationId,
                contentIds: SessionContext.SelectedContentIds,
                primaryContentId: (SessionContext.SelectedContentIds != null && SessionContext.SelectedContentIds.Length > 0)
                    ? SessionContext.SelectedContentIds[0]
                    : null,
                onComplete: onComplete);
        }

        /// <summary>
        /// Explicit-tag capture + upload — use when the caller has more
        /// accurate context than SessionContext (e.g. multi-room sessions
        /// where "current game" doesn't match SelectedContentIds[0]).
        /// </summary>
        public static void CaptureAndUpload(
            string parkId,
            string[] contentIds,
            string primaryContentId = null,
            string caption = null,
            int jpegQuality = DefaultJpegQuality,
            Action<bool, string> onComplete = null)
        {
            // Capture has to run on the render thread; pull it back to the
            // calling coroutine context once we have bytes in hand.
            RunCoroutine(CaptureAndUploadRoutine(parkId, contentIds, primaryContentId, caption, jpegQuality, onComplete));
        }

        /// <summary>
        /// Capture only — returns the encoded JPEG bytes. Use if a UX layer
        /// wants a preview before committing the upload.
        /// </summary>
        public static void CaptureJpeg(int quality, Action<byte[], int, int> onCaptured)
        {
            RunCoroutine(CaptureRoutine(quality, isPng: false, (bytes, w, h) => onCaptured?.Invoke(bytes, w, h)));
        }

        // --- Internals: capture -----------------------------------------

        private static IEnumerator CaptureAndUploadRoutine(
            string parkId,
            string[] contentIds,
            string primaryContentId,
            string caption,
            int jpegQuality,
            Action<bool, string> onComplete)
        {
            byte[] bytes = null;
            int width = 0;
            int height = 0;
            yield return CaptureRoutine(jpegQuality, isPng: false, (b, w, h) =>
            {
                bytes = b;
                width = w;
                height = h;
            });

            if (bytes == null || bytes.Length == 0)
            {
                Debug.LogError("[ScreenshotAPI] Capture produced no bytes — aborting upload.");
                ScreenshotUploaded?.Invoke(false, null, null);
                onComplete?.Invoke(false, null);
                yield break;
            }

            yield return UploadRoutine(bytes, "image/jpeg", "photo", width, height, 0,
                parkId, contentIds, primaryContentId, caption, onComplete);
        }

        private static IEnumerator CaptureRoutine(int quality, bool isPng, Action<byte[], int, int> onCaptured)
        {
            // Wait for end of frame so the eye buffer reflects the latest
            // submitted virtuals. Without this, we'd capture mid-render
            // and the composite would tear.
            yield return new WaitForEndOfFrame();

            Texture passthroughTex = TryGetPassthroughTexture();

            Camera cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[ScreenshotAPI] No Camera.main — cannot capture.");
                onCaptured?.Invoke(null, 0, 0);
                yield break;
            }

            // Output dimensions: target 1280x720 by default (4MB-ish JPEG,
            // matches typical share-image sizing). Could be made dynamic.
            int width = 1280;
            int height = 720;

            RenderTexture composite = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture virtuals = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);

            // --- Background: passthrough (if available) ----------------
            // GPU blit from passthrough texture to the composite RT. On
            // headsets without the Passthrough Camera API the blit is
            // skipped and the composite stays whatever clear color the
            // virtuals camera produces — visually equivalent to today.
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = composite;
            GL.Clear(true, true, Color.black);
            if (passthroughTex != null)
            {
                Graphics.Blit(passthroughTex, composite);
            }

            // --- Foreground: re-render Camera.main with transparent clear
            // so we can alpha-blend on top of the passthrough background.
            CameraClearFlags prevClear = cam.clearFlags;
            Color prevBg = cam.backgroundColor;
            RenderTexture prevTarget = cam.targetTexture;
            try
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.targetTexture = virtuals;
                cam.Render();
            }
            finally
            {
                cam.clearFlags = prevClear;
                cam.backgroundColor = prevBg;
                cam.targetTexture = prevTarget;
            }

            // Composite virtuals over passthrough. Graphics.Blit uses the
            // default copy shader (premultiplied alpha-aware on URP).
            Graphics.Blit(virtuals, composite);

            // --- Pull bytes off the GPU -------------------------------
            RenderTexture.active = composite;
            Texture2D readback = new Texture2D(width, height, TextureFormat.RGB24, false);
            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readback.Apply();
            RenderTexture.active = prevActive;

            RenderTexture.ReleaseTemporary(composite);
            RenderTexture.ReleaseTemporary(virtuals);

            byte[] bytes = isPng ? readback.EncodeToPNG() : readback.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
            UnityEngine.Object.Destroy(readback);

            Debug.Log($"[ScreenshotAPI] Captured {(isPng ? "PNG" : "JPG")} {width}x{height}, {bytes.Length / 1024} KB");
            onCaptured?.Invoke(bytes, width, height);
        }

        private static Texture TryGetPassthroughTexture()
        {
            // Mirrors PassthroughCameraRouter.Start() — Meta.XR is assumed
            // present (it's an unconditional `using` across the codebase).
            // On non-Quest builds the access component just won't be in
            // the scene; we fall back to virtuals-only capture. iOS AR
            // already includes the camera background in Camera.main, so
            // that path produces a correct composite without a manual blit.
            if (_cachedCameraAccess == null)
            {
                _cachedCameraAccess = UnityEngine.Object.FindFirstObjectByType<PassthroughCameraAccess>();
            }
            return _cachedCameraAccess != null ? _cachedCameraAccess.GetTexture() : null;
        }

        // --- Internals: upload ------------------------------------------

        private static IEnumerator UploadRoutine(
            byte[] bytes,
            string contentType,
            string mediaType,
            int width,
            int height,
            int durationMs,
            string parkId,
            string[] contentIds,
            string primaryContentId,
            string caption,
            Action<bool, string> onComplete)
        {
            if (!AuthAPI.isLoggedIn)
            {
                Debug.LogWarning("[ScreenshotAPI] Not logged in — cannot upload. Capture discarded.");
                ScreenshotUploaded?.Invoke(false, null, null);
                onComplete?.Invoke(false, null);
                yield break;
            }

            // ---- step 1: ask backend for a presigned PUT URL ----------
            var bodyA = new JSONObject(JSONObject.Type.Object);
            bodyA.AddField("mediaType", mediaType);
            bodyA.AddField("contentType", contentType);
            bodyA.AddField("parkId", parkId ?? "");
            bodyA.AddField("primaryContentId", primaryContentId ?? "");
            bodyA.AddField("byteSize", bytes.Length);
            if (!string.IsNullOrEmpty(SessionContext.SessionId)) bodyA.AddField("sessionId", SessionContext.SessionId);
            if (contentIds != null && contentIds.Length > 0)
            {
                var arr = new JSONObject(JSONObject.Type.Array);
                foreach (var c in contentIds) arr.Add(new JSONObject(JSONObject.Type.String) { stringValue = c });
                bodyA.AddField("contentIds", arr);
            }

            string uploadUrl = null;
            string screenshotId = null;
            bool presignOk = false;
            DreamParkAPI.POST("/api/user/screenshots/uploadUrl", AuthAPI.GetUserAuth(), bodyA, (success, response) =>
            {
                presignOk = success && response?.json != null;
                if (!presignOk)
                {
                    Debug.LogError($"[ScreenshotAPI] /uploadUrl failed: {response?.statusCode} {response?.error}");
                    return;
                }
                uploadUrl = response.json.HasField("uploadUrl") ? response.json.GetField("uploadUrl").stringValue : null;
                screenshotId = response.json.HasField("screenshotId") ? response.json.GetField("screenshotId").stringValue : null;
            });
            // Block on the callback the same way the rest of the API stack
            // does — DreamParkAPI dispatches to a coroutine runner and
            // invokes the callback synchronously on completion.
            yield return WaitForCallback(() => presignOk || uploadUrl != null, timeoutSeconds: 15f);
            if (string.IsNullOrEmpty(uploadUrl) || string.IsNullOrEmpty(screenshotId))
            {
                ScreenshotUploaded?.Invoke(false, null, null);
                onComplete?.Invoke(false, null);
                yield break;
            }

            // ---- step 2: PUT bytes straight to Firebase Storage -------
            bool putOk = false;
            DreamParkAPI.PUT(uploadUrl, "", bytes, contentType, (success, _) =>
            {
                putOk = success;
                if (!success) Debug.LogError("[ScreenshotAPI] GCS PUT failed.");
            });
            yield return WaitForCallback(() => putOk, timeoutSeconds: 60f);
            if (!putOk)
            {
                ScreenshotUploaded?.Invoke(false, screenshotId, null);
                onComplete?.Invoke(false, null);
                yield break;
            }

            // ---- step 3: commit metadata ------------------------------
            var bodyC = new JSONObject(JSONObject.Type.Object);
            bodyC.AddField("screenshotId", screenshotId);
            bodyC.AddField("width", width);
            bodyC.AddField("height", height);
            if (durationMs > 0) bodyC.AddField("durationMs", durationMs);
            if (!string.IsNullOrEmpty(caption)) bodyC.AddField("caption", caption);

            string mediaUrl = null;
            bool commitOk = false;
            DreamParkAPI.POST("/api/user/screenshots/commit", AuthAPI.GetUserAuth(), bodyC, (success, response) =>
            {
                commitOk = success && response?.json != null;
                if (!commitOk)
                {
                    Debug.LogError($"[ScreenshotAPI] /commit failed: {response?.statusCode} {response?.error}");
                    return;
                }
                if (response.json.HasField("screenshot"))
                {
                    var s = response.json.GetField("screenshot");
                    if (s.HasField("mediaUrl")) mediaUrl = s.GetField("mediaUrl").stringValue;
                }
            });
            yield return WaitForCallback(() => commitOk, timeoutSeconds: 15f);

            ScreenshotUploaded?.Invoke(commitOk, screenshotId, mediaUrl);
            onComplete?.Invoke(commitOk, mediaUrl);
            if (commitOk)
            {
                Debug.Log($"[ScreenshotAPI] ✅ Uploaded screenshot {screenshotId} → {mediaUrl}");
            }
        }

        // --- Coroutine helpers ------------------------------------------

        private static IEnumerator WaitForCallback(Func<bool> isDone, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!isDone())
            {
                if (Time.realtimeSinceStartup > deadline) yield break;
                yield return null;
            }
        }

        private static void RunCoroutine(IEnumerator routine)
        {
#if UNITY_EDITOR
            EditorCoroutineUtility.StartCoroutineOwnerless(routine);
#else
            CoroutineRunner.Run(routine);
#endif
        }
    }
}
