// ─────────────────────────────────────────────────────────────────────
//  DreamParkSecretScan.cs — SDK-synced editor tooling
//
//  Finds backend admin/Editor API keys (and the names of the code that
//  holds them) in files that are about to leave the building: the SDK
//  .unitypackage (SDK export, see DreamParkSdkDocs.StageForExport) and, in
//  dreampark-core, every player build (core's EditorKeyBuildGuard).
//
//  HASHES ONLY. This file ships in the public SDK, so it never holds a key
//  value. Known keys are listed by SHA-256; candidate strings in the scanned
//  bytes (every 44-char base64-alphabet window and every 64-char window) are
//  hashed and compared. Findings name the file and the kind of match, never
//  the matched text.
//
//  When a key is rotated or leaks, add its SHA-256 here AND to
//  scripts/git-hooks/known-key-sha256.txt in dreampark-core and dreampark-sdk:
//      printf %s "$KEY" | shasum -a 256
// ─────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DreamPark.Security
{
    public static class DreamParkSecretScan
    {
        /// <summary>SHA-256 (lowercase hex) of every known admin/Editor key, live or rotated.</summary>
        public static readonly string[] KnownKeySha256 =
        {
            "5cc48a94ffc0900b92e3d6e27fcb2745247725fd7278673dc9ec923224e75c5e", // admin key 2026-05..10 (44-char base64), leaked
            "7d36381756bcff910967c60086c97a9742f3304929f7889b0357caf03c9d6b9a", // admin key rotated 2026-10-16 (64-char hex)
        };

        /// <summary>Candidate lengths: 32-byte base64 keys and 64-char hex keys.</summary>
        public static readonly int[] DefaultWindowLengths = { 44, 64 };

        // Built by concatenation so this file itself can ship in the package it guards.
        public static readonly string[] ForbiddenSdkNames =
        {
            "Core" + "Secrets",
            "INTERNAL_" + "SECRET_KEY",
            "API_" + "SECRET_KEY",
            "UNITY_" + "SECRET_KEY",
        };

        public static readonly string[] ForbiddenSdkExtensions = { ".docx", ".pdf", ".xlsx", ".pptx" };

        private const string DeviceKeyPrefix = "hsk_";
        private const int DeviceKeyMinChars = 32;

        // ── Key matching ───────────────────────────────────────────────

        /// <summary>A set of key hashes plus the window lengths to try.</summary>
        public sealed class KeySet
        {
            internal readonly HashSet<string> hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // First 8 bytes of each hash: a cheap pre-filter so most windows never build a hex string.
            internal readonly HashSet<ulong> prefixes = new HashSet<ulong>();
            internal readonly SortedSet<int> lengths = new SortedSet<int>();
            public int Count => hashes.Count;

            public static KeySet Known()
            {
                var set = new KeySet();
                foreach (string h in KnownKeySha256) set.AddHash(h);
                foreach (int l in DefaultWindowLengths) set.lengths.Add(l);
                return set;
            }

            /// <summary>Also match this exact value (e.g. the key read from the local key file).</summary>
            public void AddValue(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                AddHash(Sha256Hex(Encoding.UTF8.GetBytes(value)));
                lengths.Add(Encoding.UTF8.GetByteCount(value));
            }

            public void AddHash(string sha256Hex)
            {
                if (string.IsNullOrEmpty(sha256Hex) || sha256Hex.Length != 64) return;
                hashes.Add(sha256Hex.ToLowerInvariant());
                prefixes.Add(Convert.ToUInt64(sha256Hex.Substring(0, 16), 16));
            }

            internal bool Matches(byte[] hash)
            {
                ulong p = 0;
                for (int i = 0; i < 8; i++) p = (p << 8) | hash[i];
                return prefixes.Contains(p) && hashes.Contains(ToHex(hash));
            }
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(bytes));
        }

        private static string ToHex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static bool IsKeyChar(byte b) =>
            (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'0' && b <= (byte)'9') ||
            b == (byte)'+' || b == (byte)'/' || b == (byte)'=' || b == (byte)'-' || b == (byte)'_';

        /// <summary>
        /// True when any run of key-alphabet characters in `data` contains a window whose
        /// SHA-256 is in `keys`. Windows (not whole runs) because IL2CPP metadata stores string
        /// literals back to back with no separator. `utf16` also checks UTF-16LE text (the
        /// user-string heap of managed DLLs) at both byte alignments.
        /// </summary>
        public static bool ContainsKey(byte[] data, KeySet keys, bool utf16)
        {
            if (data == null || keys == null || keys.Count == 0 || keys.lengths.Count == 0) return false;
            using (var sha = SHA256.Create())
            {
                if (ScanStride(data, 0, 1, keys, sha)) return true;
                if (utf16 && (ScanStride(data, 0, 2, keys, sha) || ScanStride(data, 1, 2, keys, sha))) return true;
            }
            return false;
        }

        private static bool ScanStride(byte[] data, int offset, int stride, KeySet keys, SHA256 sha)
        {
            int minLen = keys.lengths.Min;
            int n = data.Length;
            int runStart = 0, runLen = 0;
            for (int i = offset; i + stride - 1 < n; i += stride)
            {
                bool ok = IsKeyChar(data[i]) && (stride == 1 || data[i + 1] == 0);
                if (ok)
                {
                    if (runLen == 0) runStart = i;
                    runLen++;
                    continue;
                }
                if (runLen >= minLen && CheckRun(data, runStart, runLen, stride, keys, sha)) return true;
                runLen = 0;
            }
            return runLen >= minLen && CheckRun(data, runStart, runLen, stride, keys, sha);
        }

        private static bool CheckRun(byte[] data, int start, int runLen, int stride, KeySet keys, SHA256 sha)
        {
            byte[] run;
            if (stride == 1)
            {
                run = data;
            }
            else
            {
                run = new byte[runLen];
                for (int k = 0; k < runLen; k++) run[k] = data[start + k * stride];
                start = 0;
            }
            foreach (int len in keys.lengths)
            {
                if (len > runLen) break;
                for (int s = 0; s + len <= runLen; s++)
                    if (keys.Matches(sha.ComputeHash(run, start + s, len))) return true;
            }
            return false;
        }

        // ── Byte search helpers ────────────────────────────────────────

        public static bool ContainsAscii(byte[] data, string needle)
        {
            byte[] pat = Encoding.ASCII.GetBytes(needle);
            if (pat.Length == 0 || data.Length < pat.Length) return false;
            int last = data.Length - pat.Length;
            for (int i = Array.IndexOf(data, pat[0]); i >= 0 && i <= last; i = Array.IndexOf(data, pat[0], i + 1))
            {
                int k = 1;
                while (k < pat.Length && data[i + k] == pat[k]) k++;
                if (k == pat.Length) return true;
                if (i == last) break;
            }
            return false;
        }

        private static bool IsHex(byte b) =>
            (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f') || (b >= (byte)'A' && b <= (byte)'F');

        /// <summary>"hsk_" followed by at least 32 hex characters (a device key, not prose like "hsk_*").</summary>
        public static bool ContainsDeviceKeyShape(byte[] data)
        {
            byte[] pat = Encoding.ASCII.GetBytes(DeviceKeyPrefix);
            int last = data.Length - pat.Length;
            for (int i = Array.IndexOf(data, pat[0]); i >= 0 && i <= last; i = Array.IndexOf(data, pat[0], i + 1))
            {
                int k = 1;
                while (k < pat.Length && data[i + k] == pat[k]) k++;
                if (k < pat.Length) { if (i == last) break; continue; }
                int j = i + pat.Length, hex = 0;
                while (j < data.Length && IsHex(data[j])) { hex++; j++; }
                if (hex >= DeviceKeyMinChars) return true;
                if (i == last) break;
            }
            return false;
        }

        // ── SDK package scan ───────────────────────────────────────────

        /// <summary>
        /// Every reason the folder `root` (the SDK package source, Assets/DreamPark, with the
        /// docs already staged) must not be exported. Empty = clean. Skips what Unity's
        /// exporter skips: names starting with '.' and folders ending in '~'.
        /// </summary>
        public static List<string> ScanSdkPackage(string root)
        {
            var problems = new List<string>();
            if (!Directory.Exists(root))
            {
                problems.Add(root + ": package folder not found");
                return problems;
            }
            KeySet keys = KeySet.Known();
            string fullRoot = Path.GetFullPath(root);
            foreach (string file in EnumeratePackageFiles(fullRoot))
            {
                string rel = file.Substring(fullRoot.Length).TrimStart('/', '\\').Replace('\\', '/');
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(ForbiddenSdkExtensions, ext) >= 0)
                {
                    problems.Add(rel + ": office/PDF document (" + ext + ") — documents never ship in the SDK");
                    continue;
                }
                byte[] data;
                try { data = File.ReadAllBytes(file); }
                catch (Exception e)
                {
                    problems.Add(rel + ": could not be read (" + e.GetType().Name + ")");
                    continue;
                }
                foreach (string name in ForbiddenSdkNames)
                    if (ContainsAscii(data, name))
                        problems.Add(rel + ": names core-only secret code or config (" + name + ")");
                if (ContainsDeviceKeyShape(data))
                    problems.Add(rel + ": contains an " + DeviceKeyPrefix + " device-key-shaped token");
                if (ContainsKey(data, keys, ext == ".dll"))
                    problems.Add(rel + ": contains a known admin/Editor API key (matched by SHA-256)");
            }
            return problems;
        }

        private static List<string> EnumeratePackageFiles(string root)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception) { continue; }
                foreach (string f in files)
                    if (!Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal)) result.Add(f);
                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) continue;
                    pending.Push(d);
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }
}
#endif
