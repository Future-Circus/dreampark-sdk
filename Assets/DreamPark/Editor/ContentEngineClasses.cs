#if UNITY_EDITOR
// ─────────────────────────────────────────────────────────────────────
//  ContentEngineClasses.cs — which Unity engine classes title content uses, and whether
//  dreampark-core keeps them in its player builds (sal-daisy, Oct 23 2026).
//
//  Core builds with engine code stripping: the player keeps only the native engine
//  classes core's own code / scenes name, plus what core's Assets/link.xml preserves.
//  Content arrives in remote bundles, so a component class used ONLY by content is
//  stripped and silently never loads on a device: ConfigurableJoint (SuperAdventureLand's
//  daisies and palms flew apart, Oct 2026), AudioReverbFilter on Quest before it.
//
//  ContentEngineClasses.json (next to this file, generated in core by
//  Tools/agent/content_engine_classes.py from every title repo) lists every engine class
//  core preserves for content. Core's ContentEngineClassesTests checks link.xml keeps them
//  all; a title's EngineClassStripCheck preflight warns about a class its content uses
//  that is NOT in the list (ask core to rerun the script and ship a player build).
// ─────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DreamPark
{
    public static class ContentEngineClasses
    {
        public const string ManifestPath = "Assets/DreamPark/Editor/ContentEngineClasses.json";

        [Serializable]
        public sealed class Entry
        {
            public string name;
            public string type;
            public int classId;
            public string assembly;
        }

        [Serializable]
        private sealed class Manifest
        {
            public string[] repos;
            public Entry[] classes;
        }

        /// Old serialized names of renamed classes (same class ID).
        public static readonly Dictionary<string, string> Alias = new Dictionary<string, string> { { "PhysicMaterial", "PhysicsMaterial" } };

        /// YAML asset kinds whose objects ship in a content bundle (scenes don't: titles ship prefabs).
        public static readonly HashSet<string> ShippingExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".asset", ".mat", ".controller", ".overrideController", ".anim", ".mask", ".physicMaterial",
            ".physicsMaterial", ".playable", ".renderTexture", ".flare", ".lighting", ".spriteatlas", ".spriteatlasv2",
            ".terrainlayer", ".signal", ".mixer", ".cubemap", ".fontsettings", ".guiskin",
        };

        private static readonly Regex Header = new Regex(@"^--- !u!(\d+) &-?\d+(?: stripped)?\s*$", RegexOptions.Compiled);

        public static List<Entry> Load(out string[] repos)
        {
            repos = Array.Empty<string>();
            string path = Path.GetFullPath(ManifestPath);
            if (!File.Exists(path)) return null;
            var m = JsonUtility.FromJson<Manifest>(File.ReadAllText(path));
            repos = m?.repos ?? Array.Empty<string>();
            return m?.classes?.ToList() ?? new List<Entry>();
        }

        /// Class name -> files (YAML headers `--- !u!<id>` and the "Name:" line after each) under <paramref name="root"/>.
        public static Dictionary<string, List<string>> ScanClassNames(string root, Func<string, bool> include = null)
        {
            var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (!Directory.Exists(root)) return used;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!ShippingExtensions.Contains(Path.GetExtension(file))) continue;
                string p = file.Replace('\\', '/');
                if (include != null && !include(p)) continue;
                try
                {
                    using (var r = new StreamReader(p))
                    {
                        string line = r.ReadLine();
                        if (line == null || !line.StartsWith("%YAML", StringComparison.Ordinal)) continue;
                        bool next = false;
                        var seen = new HashSet<string>();
                        while ((line = r.ReadLine()) != null)
                        {
                            if (next)
                            {
                                next = false;
                                string name = line.Trim().TrimEnd(':');
                                if (Alias.TryGetValue(name, out string a)) name = a;
                                if (seen.Add(name))
                                {
                                    if (!used.TryGetValue(name, out var list)) used[name] = list = new List<string>();
                                    list.Add(p);
                                }
                                continue;
                            }
                            if (line.Length > 6 && line[0] == '-' && Header.IsMatch(line)) next = true;
                        }
                    }
                }
                catch (IOException) { }
            }
            return used;
        }

        private static Dictionary<string, Type> s_runtime;

        /// The RUNTIME engine type of a native class name (UnityEngine[.*] namespace, a UnityEngine.Object, in a
        /// UnityEngine.* module), or null: editor-only classes (AnimatorStateMachine, PrefabInstance …) have none.
        public static Type RuntimeType(string name)
        {
            if (s_runtime == null)
            {
                s_runtime = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an = asm.GetName().Name;
                    if (!an.StartsWith("UnityEngine.", StringComparison.Ordinal) || an.StartsWith("UnityEngine.TestRunner", StringComparison.Ordinal)) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.Namespace == null || !(t.Namespace == "UnityEngine" || t.Namespace.StartsWith("UnityEngine.", StringComparison.Ordinal))) continue;
                        if (!typeof(UnityEngine.Object).IsAssignableFrom(t) || t.IsNested) continue;
                        if (!s_runtime.TryGetValue(t.Name, out var prev) || (t.Namespace == "UnityEngine" && prev.Namespace != "UnityEngine"))
                            s_runtime[t.Name] = t;
                    }
                }
            }
            return s_runtime.TryGetValue(name, out var hit) ? hit : null;
        }

        /// Engine classes used under <paramref name="root"/> that the manifest (core's preserved set) does not list.
        public static List<(string name, Type type, List<string> files)> Unpreserved(string root, List<Entry> manifest,
                                                                                   Func<string, bool> include = null)
        {
            var known = new HashSet<string>((manifest ?? new List<Entry>()).Select(e => e.name), StringComparer.Ordinal);
            var outList = new List<(string, Type, List<string>)>();
            foreach (var kv in ScanClassNames(root, include).OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (known.Contains(kv.Key)) continue;
                Type t = RuntimeType(kv.Key);
                if (t == null) continue;            // editor-only: nothing ships
                outList.Add((kv.Key, t, kv.Value));
            }
            return outList;
        }
    }
}
#endif
