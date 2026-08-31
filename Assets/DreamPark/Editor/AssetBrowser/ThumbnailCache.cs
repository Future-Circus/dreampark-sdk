#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.EditorCoroutines.Editor;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>
    /// Remote thumbnails for the grid.
    ///
    /// ── THIS IS NEW GROUND ─────────────────────────────────────────────────
    ///
    /// Nothing in the DreamPark SDK loads a remote image today — there is not
    /// one UnityWebRequestTexture in the tree — so there is no house pattern
    /// to copy and the two failure modes are worth naming up front:
    ///
    ///   1. LEAKS. A Texture2D created from script is not owned by the asset
    ///      database and is not collected when the window closes. Sixty of
    ///      them per page, across a session of browsing, is real memory that
    ///      only an editor restart reclaims. Dispose() destroys every one, and
    ///      the window MUST call it from OnDisable — PreviewEditorWindow does
    ///      exactly this for its single preview texture, for the same reason.
    ///
    ///   2. REQUEST STORMS. A page is sixty rows. Firing sixty concurrent
    ///      image requests starves the far more important thing happening on
    ///      the same connection pool (the download the creator actually
    ///      asked for), so fetches are queued behind a small budget.
    ///
    /// Bytes are also cached to disk under Library/, so scrolling back up, or
    /// closing and reopening the window, costs nothing. Library/ is already
    /// gitignored and is the right home for a derived cache — nothing here is
    /// worth committing and none of it may enter Assets/, where Unity would
    /// import it as a project asset.
    /// </summary>
    public class ThumbnailCache : IDisposable
    {
        /// <summary>
        /// How many thumbnails may be in flight. Small on purpose: a
        /// thumbnail is decoration, and the bulk download the creator is
        /// waiting on is not.
        /// </summary>
        private const int MaxConcurrent = 4;

        /// <summary>
        /// A hard ceiling on live textures. Beyond this the least-recently
        /// touched are destroyed — an unbounded cache across a long browsing
        /// session is the leak, just slower.
        /// </summary>
        private const int MaxLiveTextures = 300;

        private readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        private readonly Dictionary<string, int> _touched = new Dictionary<string, int>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly HashSet<string> _failed = new HashSet<string>();
        private readonly Queue<string> _pending = new Queue<string>();
        private int _clock;
        private bool _disposed;

        /// <summary>
        /// Raised when a texture arrives. The window wires this to Repaint —
        /// an async callback must never draw, only mark and request a repaint.
        /// </summary>
        public event Action Changed;

        /// <summary>
        /// The texture for a URL, or null while it is loading or if it failed.
        /// Requests the fetch on first ask. Callers draw a placeholder for
        /// null rather than skipping the cell — a grid whose cells appear as
        /// they load reflows under the pointer.
        /// </summary>
        public Texture2D Get(string url)
        {
            if (_disposed || string.IsNullOrEmpty(url)) return null;

            if (_textures.TryGetValue(url, out var tex))
            {
                _touched[url] = ++_clock;
                // A texture destroyed underneath us (domain reload, or our own
                // trim) compares equal to null through Unity's operator. Drop
                // the stale entry and re-fetch rather than handing back a
                // corpse.
                if (tex != null) return tex;
                _textures.Remove(url);
                _touched.Remove(url);
            }

            if (_failed.Contains(url) || _inFlight.Contains(url)) return null;

            _inFlight.Add(url);
            _pending.Enqueue(url);
            Pump();
            return null;
        }

        public bool Failed(string url) => !string.IsNullOrEmpty(url) && _failed.Contains(url);

        /// <summary>Forget a failure so the next repaint tries again.</summary>
        public void Retry(string url)
        {
            if (!string.IsNullOrEmpty(url)) _failed.Remove(url);
        }

        private int _running;

        private void Pump()
        {
            while (!_disposed && _running < MaxConcurrent && _pending.Count > 0)
            {
                string url = _pending.Dequeue();
                _running++;
                EditorCoroutineUtility.StartCoroutineOwnerless(Fetch(url));
            }
        }

        private IEnumerator Fetch(string url)
        {
            /* ── the slot must come back, whatever happens ─────────────────
             *
             * `_running` is the whole concurrency budget. If this coroutine
             * dies before decrementing it — a malformed preview URL is enough
             * to make UnityWebRequest.Get throw — that slot is gone for the
             * session. Four such rows exhaust MaxConcurrent, Pump() can never
             * start another fetch, and every thumbnail from then on renders as
             * "…" forever with nothing in the console.
             *
             * try/finally, not try/catch: a `yield return` is legal in a try
             * with only a finally.
             */
            byte[] bytes = null;
            try
            {
                bytes = ReadDisk(url);

                if (bytes == null)
                {
                    using (var req = UnityWebRequest.Get(url))
                    {
                        req.downloadHandler = new DownloadHandlerBuffer();
                        req.timeout = 20;
                        yield return req.SendWebRequest();

                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            bytes = req.downloadHandler.data;
                            WriteDisk(url, bytes);
                        }
                    }
                }
            }
            finally
            {
                _running--;
                _inFlight.Remove(url);
            }

            if (_disposed) { Pump(); yield break; }

            if (bytes == null || bytes.Length == 0)
            {
                // Quietly. A missing thumbnail is a cosmetic problem on a page
                // that may show sixty of them; logging each one would bury the
                // console under noise about decoration.
                _failed.Add(url);
            }
            else
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    // Not owned by the asset database, so it must be told not
                    // to serialize into anything.
                    hideFlags = HideFlags.HideAndDontSave,
                };
                if (tex.LoadImage(bytes))
                {
                    tex.filterMode = FilterMode.Bilinear;
                    _textures[url] = tex;
                    _touched[url] = ++_clock;
                    Trim();
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    _failed.Add(url);
                }
            }

            Changed?.Invoke();
            Pump();
        }

        private void Trim()
        {
            if (_textures.Count <= MaxLiveTextures) return;

            var byAge = new List<KeyValuePair<string, int>>(_touched);
            byAge.Sort((a, b) => a.Value.CompareTo(b.Value));

            int toDrop = _textures.Count - MaxLiveTextures;
            for (int i = 0; i < byAge.Count && toDrop > 0; i++)
            {
                string url = byAge[i].Key;
                if (!_textures.TryGetValue(url, out var tex)) continue;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                _textures.Remove(url);
                _touched.Remove(url);
                toDrop--;
                // The bytes stay on disk, so a trimmed thumbnail scrolled back
                // into view reloads instantly and without the network.
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var tex in _textures.Values)
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            _textures.Clear();
            _touched.Clear();
            _pending.Clear();
            _inFlight.Clear();
            Changed = null;
        }

        /* ── disk cache ────────────────────────────────────────────────── */

        private static string CacheRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            return Path.Combine(projectRoot, "Library", "DreamPark", "Dreamie", "thumbs");
        }

        private static string CachePath(string url)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(url));
                var sb = new StringBuilder(32);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return Path.Combine(CacheRoot(), sb.ToString());
            }
        }

        private static byte[] ReadDisk(string url)
        {
            try
            {
                string p = CachePath(url);
                return File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            catch { return null; }
        }

        private static void WriteDisk(string url, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            try
            {
                Directory.CreateDirectory(CacheRoot());
                File.WriteAllBytes(CachePath(url), bytes);
            }
            catch
            {
                // A cache that cannot write is a slower cache, not a broken
                // window. Say nothing.
            }
        }

        /// <summary>Wipe the on-disk thumbnails. Offered in the window's menu.</summary>
        public static void ClearDiskCache()
        {
            try
            {
                if (Directory.Exists(CacheRoot())) Directory.Delete(CacheRoot(), true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dreamie] Could not clear the thumbnail cache: {e.Message}");
            }
        }
    }
}
#endif
