#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>One asset's journey from the library into the project.</summary>
    public class DreamieDownloadItem
    {
        public DreamieAsset asset;
        public string jobSlug;
        public string stem;

        public string stagedFbxPath;      // in Library/, outside the asset pipeline
        public string targetFbxPath;      // in Assets/, once accepted

        public bool downloaded;
        public bool imported;
        public string error;
        public List<string> warnings = new List<string>();
    }

    public class DreamieDownloadSummary
    {
        public int downloaded;
        public int imported;
        public int failed;
        public int skipped;
        public List<DreamieDownloadItem> items = new List<DreamieDownloadItem>();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(imported).Append(imported == 1 ? " asset imported" : " assets imported");
            if (skipped > 0) sb.Append(", ").Append(skipped).Append(" already present");
            if (failed > 0) sb.Append(", ").Append(failed).Append(" failed");
            return sb.ToString() + ".";
        }
    }

    /// <summary>
    /// Bulk download and import.
    ///
    /// ── THE TWO PHASES, AND WHY THEY ARE SEPARATE ──────────────────────────
    ///
    /// PHASE 1 is the network, and it writes only into Library/DreamPark/
    /// Dreamie/staging — deliberately outside Assets/. A partially written FBX
    /// inside Assets/ gets imported by Unity the moment focus returns to the
    /// editor, producing a broken asset with a real GUID that something might
    /// reference before the download has even finished failing. Staging makes
    /// a failed download a no-op rather than debris.
    ///
    /// It also means PHASE 2 — moving accepted files in and importing them —
    /// is entirely synchronous, which is what lets the whole thing sit inside
    /// ContentProcessor.ExecuteWithWatchdogPaused. That matters: dropping
    /// thirty FBX files into a content folder otherwise wakes the folder
    /// watchdog thirty times, and each wake is a full content-stamping pass.
    /// The watchdog helper is ref-counted and exception-safe and takes an
    /// Action, so it cannot wrap a coroutine — the phase split is what makes
    /// it usable at all.
    /// </summary>
    public static class DreamieDownloader
    {
        // Lifted from ContentAPI's upload path so transfers behave the same in
        // both directions rather than each inventing a policy.
        private const int MaxAttempts = 3;
        private const int MaxConcurrent = 6;

        /// <summary>
        /// Download and import a selection. `onDone` fires on the editor
        /// thread once everything has settled.
        /// </summary>
        public static void Run(
            IList<DreamieAsset> assets,
            string contentId,
            IDictionary<string, DreamieJob> jobsById,
            Action<float, string> onProgress,
            Action<DreamieDownloadSummary> onDone)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(
                RunRoutine(assets, contentId, jobsById, onProgress, onDone));
        }

        private static IEnumerator RunRoutine(
            IList<DreamieAsset> assets,
            string contentId,
            IDictionary<string, DreamieJob> jobsById,
            Action<float, string> onProgress,
            Action<DreamieDownloadSummary> onDone)
        {
            var summary = new DreamieDownloadSummary();
            var plan = new List<DreamieDownloadItem>();

            foreach (var a in assets)
            {
                if (a == null) continue;
                string jobSlug = SlugForJob(a, jobsById);
                string stem = DreamieFolders.StemFor(a);
                plan.Add(new DreamieDownloadItem
                {
                    asset = a,
                    jobSlug = jobSlug,
                    stem = stem,
                    stagedFbxPath = Path.Combine(DreamieFolders.StagingRoot(), stem, stem + ".fbx"),
                    targetFbxPath = DreamieFolders.ModelPathFor(contentId, jobSlug, stem),
                });
            }
            summary.items.AddRange(plan);

            /* ── phase 1: network ─────────────────────────────────────── */

            int done = 0;
            int running = 0;
            int next = 0;

            while (done < plan.Count)
            {
                while (running < MaxConcurrent && next < plan.Count)
                {
                    var item = plan[next++];
                    running++;
                    EditorCoroutineUtility.StartCoroutineOwnerless(
                        DownloadOne(item, () => { running--; done++; }));
                }

                onProgress?.Invoke(
                    plan.Count == 0 ? 1f : (float)done / plan.Count,
                    $"Downloading {Mathf.Min(done + 1, plan.Count)} of {plan.Count}…");
                yield return null;
            }

            foreach (var item in plan)
            {
                if (item.downloaded) summary.downloaded++;
                else summary.failed++;
            }

            /* ── phase 2: into the project ────────────────────────────── */

            onProgress?.Invoke(1f, "Importing…");

            // One synchronous block. See the class comment for why this can be
            // wrapped and phase 1 cannot.
            ContentProcessor.ExecuteWithWatchdogPaused(() =>
            {
                try
                {
                    AssetDatabase.DisallowAutoRefresh();
                    for (int i = 0; i < plan.Count; i++)
                    {
                        var item = plan[i];
                        if (!item.downloaded) continue;

                        EditorUtility.DisplayProgressBar(
                            "DreamPark Asset Browser",
                            $"Importing {item.asset.name} ({i + 1}/{plan.Count})",
                            (float)i / Mathf.Max(1, plan.Count));

                        try
                        {
                            InstallOne(item, contentId);
                            if (item.imported) summary.imported++;
                            else summary.failed++;
                        }
                        catch (Exception e)
                        {
                            item.error = e.Message;
                            summary.failed++;
                            Debug.LogError($"[Dreamie] {item.asset.name} failed to import: {e}");
                        }
                    }
                }
                finally
                {
                    AssetDatabase.AllowAutoRefresh();
                    EditorUtility.ClearProgressBar();
                    AssetDatabase.Refresh();
                }
            });

            onDone?.Invoke(summary);
        }

        private static string SlugForJob(DreamieAsset a, IDictionary<string, DreamieJob> jobsById)
        {
            if (string.IsNullOrEmpty(a.assetJobId)) return DreamieFolders.SinglesFolder;
            if (jobsById != null && jobsById.TryGetValue(a.assetJobId, out var job) && job != null)
                return DreamieFolders.JobSlugFor(job);
            // The asset says it belongs to a batch we could not look up — the
            // job aged out of the list, or /jobs degraded. Keep the batch
            // together under its id rather than scattering it into _singles.
            return DreamieFolders.JobSlugForId(a.assetJobId);
        }

        /* ── phase 1 ───────────────────────────────────────────────────── */

        private static IEnumerator DownloadOne(DreamieDownloadItem item, Action onFinished)
        {
            /* ── onFinished MUST run, whatever happens ─────────────────────
             *
             * RunRoutine's loop waits on `done` reaching plan.Count. If this
             * coroutine dies before signalling — a bad path from
             * CreateDirectory, a file handle that will not open, a malformed
             * URL from the server — the counter never advances and the outer
             * loop yields FOREVER: an immortal editor coroutine, a progress
             * bar stuck at 40%, and a Download button disabled for the rest of
             * the session with nothing in the console to explain it.
             *
             * try/finally, not try/catch: `yield return` is legal inside a try
             * that has only a finally, which is exactly the shape needed here.
             */
            try
            {
                string url = item.asset.UrlForFormat("fbx", out _);
                if (string.IsNullOrEmpty(url))
                {
                    item.error = "No FBX — this asset ships no format Unity can import.";
                    yield break;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(item.stagedFbxPath));

                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    if (attempt > 0)
                    {
                        // 1s, 2s. Same shape as ContentAPI's upload backoff.
                        double wait = Math.Pow(2, attempt) * 0.5;
                        double until = EditorApplication.timeSinceStartup + wait;
                        while (EditorApplication.timeSinceStartup < until) yield return null;
                    }

                    bool fatal = false;
                    using (var req = UnityWebRequest.Get(url))
                    {
                        // Straight to disk — a 200MB master must never be
                        // buffered in memory, and thirty of them concurrently
                        // certainly not.
                        req.downloadHandler = new DownloadHandlerFile(item.stagedFbxPath) { removeFileOnAbort = true };
                        req.timeout = 300;

                        // NO Authorization header. These are public bucket
                        // URLs; attaching a DreamPark bearer token would send
                        // a credential to Google's storage host, which rejects
                        // it. The download counter is pinged separately — see
                        // DreamieCatalogApi.RecordDownload.
                        yield return req.SendWebRequest();

                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            item.error = null;
                            break;
                        }

                        item.error = req.error;
                        // A 4xx is an answer, not a hiccup. Retrying a 404 two
                        // more times with backoff just makes the same wrong
                        // result take four seconds longer to report.
                        fatal = req.responseCode >= 400 && req.responseCode < 500;
                    }
                    if (fatal) break;
                }

                if (item.error != null)
                {
                    // Every attempt failed. Do not leave a truncated file in
                    // staging for a later run to find and trust.
                    TryDelete(item.stagedFbxPath);
                    yield break;
                }

                // WHAT IT IS, NOT WHAT IT IS CALLED.
                //
                // Dreamie's own publisher guards this on the way out, because
                // a Tripo convert-to-FBX task echoes the SOURCE model's URL
                // alongside the converted file and taking the wrong one writes
                // a GLB under an .fbx name. That check is upstream and should
                // hold — but the failure it prevents surfaces as an unopenable
                // asset inside Unity, which is precisely the worst place to
                // discover it, so four bytes are worth re-reading on this side
                // of the wire too.
                string actual = SniffModelFormat(item.stagedFbxPath);
                if (actual != "fbx")
                {
                    item.error = $"The library returned a {actual.ToUpperInvariant()} where an FBX was expected. "
                                 + "Re-run the model step in Dreamie for this asset.";
                    TryDelete(item.stagedFbxPath);
                    yield break;
                }

                item.downloaded = true;
                DreamieCatalogApi.RecordDownload(item.asset.id);
            }
            finally
            {
                onFinished();
            }
        }

        /// <summary>
        /// Identify a mesh file by its leading bytes. Mirrors
        /// dreamie/util/meshStats.js sniffModelFormat.
        /// </summary>
        private static string SniffModelFormat(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var head = new byte[24];
                    int read = fs.Read(head, 0, head.Length);
                    if (read <= 0) return "unknown";
                    string ascii = Encoding.ASCII.GetString(head, 0, read);

                    // Binary FBX opens with a fixed 20-byte signature; the
                    // ASCII flavour opens with a comment naming itself.
                    if (ascii.StartsWith("Kaydara FBX Binary")) return "fbx";
                    if (ascii.IndexOf("FBX", StringComparison.Ordinal) >= 0 && ascii.TrimStart().StartsWith(";")) return "fbx";
                    if (ascii.StartsWith("glTF")) return "glb";
                    if (read >= 4 && head[0] == 0x50 && head[1] == 0x4b) return "zip";
                    return "unknown";
                }
            }
            catch { return "unknown"; }
        }

        /* ── phase 2 ───────────────────────────────────────────────────── */

        private static void InstallOne(DreamieDownloadItem item, string contentId)
        {
            string assetFolder = DreamieFolders.AssetFolderFor(contentId, item.jobSlug, item.stem);
            DreamieFolders.EnsureFolder(assetFolder);

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            string absTarget = Path.Combine(projectRoot, item.targetFbxPath.Replace('/', Path.DirectorySeparatorChar));

            // Overwrite in place when this asset is already here. Replacing the
            // bytes keeps the .meta, and therefore the GUID — so every prefab,
            // material and scene already pointing at this mesh keeps working.
            // Deleting and re-adding would mint a new GUID and silently break
            // all of them, which is the single most destructive thing a
            // re-download could do.
            Directory.CreateDirectory(Path.GetDirectoryName(absTarget));
            File.Copy(item.stagedFbxPath, absTarget, true);
            TryDelete(item.stagedFbxPath);

            AssetDatabase.ImportAsset(item.targetFbxPath, ImportAssetOptions.ForceSynchronousImport);

            var record = new DreamieProvenanceRecord
            {
                assetId = item.asset.id,
                jobId = item.asset.assetJobId,
                name = item.asset.name,
                creatorId = item.asset.creatorId,
                creatorName = item.asset.creatorName,
                glbUrl = item.asset.GlbUrl,
                fbxUrl = item.asset.UrlForFormat("fbx"),
                downloadedUtc = DateTime.UtcNow.ToString("o"),
                tags = item.asset.tags?.ToArray(),
            };

            var result = DreamieImportPass.Run(item.targetFbxPath, record, contentId);
            item.warnings.AddRange(result.warnings);
            if (!result.ok)
            {
                item.error = result.error;
                return;
            }

            DreamieFolders.Record(contentId, item.targetFbxPath, item.asset);
            item.imported = true;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* staging litter is harmless */ }
        }
    }
}
#endif
