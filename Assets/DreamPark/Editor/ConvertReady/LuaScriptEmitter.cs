// ─────────────────────────────────────────────────────────────────────
//  LuaScriptEmitter.cs — the converter's output is a Lua script, not a
//  component.
//
//  WHY
//
//  Every pack used to end in a C# MonoBehaviour the creator could
//  configure and nothing else: a shatter component's fields, an
//  Interactable's filter array. That is a dead end by design. A creator who wants the
//  prop to do something ELSE when it breaks — score it, play a sound,
//  spawn a pickup, tell the attraction — has nowhere to put that. The
//  best case was "wire a UnityEvent", which does not diff, does not grep,
//  and can only be read by clicking through an inspector.
//
//  So the converter emits THE script for the prop. It happens to be
//  shatter-oriented, or collision-oriented, depending on the pack. It
//  lands in the prop's own kit folder under the prop's own name, and it
//  is the obvious place to type the next line of the game. Expansion
//  beats configuration.
//
//  This is also what the rest of the platform already assumes: storage,
//  multiplayer, the whole dp API and attraction scoping are Lua-only
//  surfaces, and Lua ships over the air — a content-only update can
//  change how a prop behaves without an app release. A behaviour baked
//  into a prefab's serialized wiring can do none of that.
//
//  THE TEMPLATES ARE REAL LUA FILES, NOT STRING CONSTANTS
//
//  LuaTemplates/*.lua.txt are valid, parseable Lua that a human can read,
//  edit and syntax-check in place. The only substitution is {PROP} — and
//  it appears exclusively inside comments and string literals, so the
//  template on disk stays legal Lua and can be linted as-is. Nothing here
//  builds code by concatenation.
//
//  They live under Editor/ deliberately: they are authoring inputs, they
//  must not ship in a player build, and LuaSurfaceScanner only scans
//  Assets/Content — so a template is never mistaken for content.
//
//  WHY THE ANCHOR AND NOT THE ROOT
//
//  LuaMessageRelays.Bind is called with the LuaBehaviour's OWN GameObject
//  and adds its collision relay there — no ancestor walk, no search, no
//  warning when there is neither a collider nor a body. Unity delivers
//  OnCollisionEnter to the collider's GameObject and to the GameObject of
//  that collider's attached Rigidbody. Both are the Anchor: the fitted
//  collider is always there, and the body goes there too because nothing
//  the converter emits may own the prop ROOT's transform (the park loader
//  places that). So collisions arrive at the Anchor whether or not the
//  plan asked for a body at all — on the root, a prop converted without a
//  Rigidbody would go silently dead.
//
//  RE-CONVERT
//
//  An existing script is never overwritten and never re-wired away. In
//  practice nobody converts the same FBX twice — the prop is the artifact
//  from then on — but the cost of being wrong here is a creator's
//  afternoon, and the cost of the guard is one File.Exists.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class LuaScriptEmitter
    {
        public const string TemplateFolder = "Assets/DreamPark/Editor/ConvertReady/LuaTemplates";
        public const string ScriptExtension = ".lua.txt";

        /// The only token substituted into a template. Appears exclusively
        /// inside Lua comments and string literals, so the template file is
        /// itself valid Lua and can be linted before it is ever copied.
        public const string PropToken = "{PROP}";

        public const string ShatterTemplate = "shatter";
        public const string InteractiveTemplate = "interactive";
        public const string BillboardTemplate = "billboard";
        public const string BendTemplate = "bend";

        // ── Public entry points ─────────────────────────────────────────

        /// <summary>
        /// Shatterable. <paramref name="anchor"/> hosts the script (see the
        /// header), <paramref name="intactVisual"/> and
        /// <paramref name="shatteredRoot"/> become GameObject injections.
        /// Returns the asset path, or null when nothing was written.
        /// </summary>
        public static string EmitShatter(GameObject anchor, GameObject intactVisual,
                                         GameObject shatteredRoot, string propName,
                                         ConversionPlan plan, ConversionResult r)
        {
            var floats = new Dictionary<string, float>
            {
                { "minImpactSpeed", plan != null ? plan.fracture.minImpactSpeed : 2f },
                { "burstImpulse", plan != null ? plan.fracture.burstImpulse : 2f },
                { "pieceLifetime", plan != null ? plan.fracture.pieceLifetime : 8f },
                { "despawnSeconds", 1f },
            };
            var strings = new Dictionary<string, string> { { "impactTag", string.Empty } };
            var objects = new Dictionary<string, GameObject>
            {
                { "intactVisual", intactVisual },
                { "shatteredRoot", shatteredRoot },
            };

            return Emit(anchor, propName, ShatterTemplate, objects, floats, strings, null, r);
        }

        /// <summary>
        /// Interactive. The generated script IS the collision handler — speed
        /// gate, tag gate and cooldown are visible in source rather than
        /// buried in a serialized filter array.
        /// </summary>
        public static string EmitInteractive(GameObject anchor, string propName,
                                             ConversionPlan plan, ConversionResult r)
        {
            var floats = new Dictionary<string, float>
            {
                { "minImpactSpeed", 0f },
                { "cooldownSeconds", 0.25f },
            };
            var strings = new Dictionary<string, string> { { "requireTag", string.Empty } };
            var bools = new Dictionary<string, bool>
            {
                { "playerOnly", false },
                { "logHits", true },
            };

            return Emit(anchor, propName, InteractiveTemplate, null, floats, strings, bools, r);
        }

        /// <summary>
        /// Billboard plane. Goes on the MOTION node, never the root — the park
        /// loader writes the spawned root's localRotation after Awake, so
        /// anything owning the root's rotation per frame overwrites the park's
        /// authored yaw on the first live frame and every poster in the park
        /// snaps to the same facing.
        /// </summary>
        public static string EmitBillboard(GameObject motionNode, string propName,
                                           bool yawOnly, ConversionResult r)
        {
            var bools = new Dictionary<string, bool> { { "yawOnly", yawOnly } };
            return Emit(motionNode, propName, BillboardTemplate, null, null, null, bools, r);
        }

        /// <summary>
        /// Bendy. Also the MOTION node, and for the same reason as the
        /// billboard: this one writes localRotation every frame, which is
        /// precisely what must never happen on the prop root.
        ///
        /// <paramref name="detectionRadius"/> is scaled from the model's own
        /// footprint by the caller — EasyBend's stock 0.75 m is a metre-wide
        /// trigger around a 5 cm coin.
        /// </summary>
        public static string EmitBend(GameObject motionNode, string propName,
                                      float detectionRadius, Vector3 detectionOffset,
                                      BendSettings bend, ConversionResult r)
        {
            if (bend == null) bend = new BendSettings();

            var floats = new Dictionary<string, float>
            {
                { "detectionRadius", detectionRadius },
                { "maxTiltAngle", bend.maxTiltAngle },
                { "springStrength", bend.springStrength },
                { "springDamping", bend.springDamping },
                { "influence", 1f },
                { "queryInterval", 0.05f },
            };
            var ints = new Dictionary<string, int>
            {
                // -1 is ~0: every layer. Static geometry is filtered by the
                // Rigidbody test instead of by the mask, which is the check
                // that actually distinguishes "a hand" from "the floor".
                { "detectionMask", -1 },
            };
            var bools = new Dictionary<string, bool>
            {
                { "requireRigidbody", true },
                { "ignoreTriggers", true },
            };

            var vectors = new Dictionary<string, Vector3> { { "detectionOffset", detectionOffset } };

            return Emit(motionNode, propName, BendTemplate, null, floats, null, bools, r, ints, vectors);
        }

        // ── The work ────────────────────────────────────────────────────

        static string Emit(GameObject host, string propName, string templateName,
                           Dictionary<string, GameObject> objects,
                           Dictionary<string, float> floats,
                           Dictionary<string, string> strings,
                           Dictionary<string, bool> bools,
                           ConversionResult r,
                           Dictionary<string, int> ints = null,
                           Dictionary<string, Vector3> vectors = null)
        {
            if (host == null)
            {
                Report(r, "lua: no object to put the script on");
                return null;
            }

            string folder = r != null ? r.kitFolder : null;
            if (string.IsNullOrEmpty(folder))
            {
                Report(r, "lua: no kit folder for this asset, so there is nowhere to write "
                    + propName + ScriptExtension + " — the prop has no script");
                return null;
            }
            folder = folder.Replace('\\', '/').TrimEnd('/');

            string templatePath = TemplateFolder + "/" + templateName + ScriptExtension;
            var template = AssetDatabase.LoadAssetAtPath<TextAsset>(templatePath);
            if (template == null)
            {
                Report(r, "lua: template " + templatePath + " is missing — the prop has no script. "
                    + "Re-sync Assets/DreamPark from dreampark-core.");
                return null;
            }

            if (!AssetClassifier.EnsureFolder(folder))
            {
                Report(r, "lua: could not create " + folder + " — the prop has no script");
                return null;
            }

            string safeName = AssetClassifier.SanitizeAssetName(propName, "Prop");
            string path = folder + "/" + safeName + ScriptExtension;

            // NEVER OVERWRITE. See the header. A creator's expansion of this
            // file is the whole point of generating it, and silently replacing
            // it with the stock template is the one unrecoverable thing this
            // class could do. Re-wire the reference and say what happened.
            bool existed = File.Exists(AbsolutePath(path));
            if (!existed)
            {
                try
                {
                    File.WriteAllText(AbsolutePath(path), template.text.Replace(PropToken, safeName));
                }
                catch (Exception e)
                {
                    Report(r, "lua: could not write " + path + " — " + e.Message);
                    return null;
                }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }

            var script = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (script == null)
            {
                Report(r, "lua: wrote " + path + " but Unity did not import it as a TextAsset");
                return null;
            }

            LuaBehaviour behaviour = Componentizer.DoComponent<LuaBehaviour>(host, true);
            if (behaviour == null)
            {
                Report(r, "lua: could not add a LuaBehaviour to '" + host.name + "'");
                return null;
            }

            behaviour.luaScript = script;
            ApplyInjections(behaviour, objects, floats, strings, bools, ints, vectors);
            EditorUtility.SetDirty(behaviour);

            if (existed)
            {
                Report(r, "lua: '" + safeName + ScriptExtension + "' already existed and was LEFT ALONE — "
                    + "the LuaBehaviour on '" + host.name + "' points at your version, not a fresh "
                    + "template. Delete the file and convert again if you want the stock one back.");
            }
            else
            {
                Report(r, "lua: '" + safeName + ScriptExtension + "' → " + folder
                    + ", on a LuaBehaviour on '" + host.name + "'. THIS IS THE PROP'S SCRIPT — it is "
                    + "yours to expand, and Convert will not overwrite it.");
            }

            return path;
        }

        // ── Injections ──────────────────────────────────────────────────

        /// <summary>
        /// Fill the arrays the inspector would otherwise build from the
        /// template's `-- @var` lines.
        ///
        /// This is not optional. LuaInjectionEditorGUI parses `@var` when a
        /// human opens the inspector; at bake time nobody does, so without
        /// this every knob arrives in Lua as nil. The templates defend
        /// against that at file scope anyway, but a prop whose inspector
        /// shows no fields reads as broken.
        ///
        /// Existing entries WIN. A re-run must not reset a value the creator
        /// tuned — same rule as not overwriting the script itself.
        /// </summary>
        static void ApplyInjections(LuaBehaviour behaviour,
                                    Dictionary<string, GameObject> objects,
                                    Dictionary<string, float> floats,
                                    Dictionary<string, string> strings,
                                    Dictionary<string, bool> bools,
                                    Dictionary<string, int> ints,
                                    Dictionary<string, Vector3> vectors)
        {
            if (objects != null)
            {
                var list = new List<Injection>(behaviour.injections ?? new Injection[0]);
                foreach (var kv in objects)
                {
                    if (Has(list.ConvertAll(x => x.name), kv.Key)) continue;
                    list.Add(new Injection { name = kv.Key, value = kv.Value });
                }
                behaviour.injections = list.ToArray();
            }

            if (floats != null)
            {
                var list = new List<FloatInjection>(behaviour.floatInjections ?? new FloatInjection[0]);
                foreach (var kv in floats)
                {
                    if (Has(list.ConvertAll(x => x.name), kv.Key)) continue;
                    list.Add(new FloatInjection { name = kv.Key, value = kv.Value });
                }
                behaviour.floatInjections = list.ToArray();
            }

            if (strings != null)
            {
                var list = new List<StringInjection>(behaviour.stringInjections ?? new StringInjection[0]);
                foreach (var kv in strings)
                {
                    if (Has(list.ConvertAll(x => x.name), kv.Key)) continue;
                    list.Add(new StringInjection { name = kv.Key, value = kv.Value });
                }
                behaviour.stringInjections = list.ToArray();
            }

            if (bools != null)
            {
                var list = new List<BoolInjection>(behaviour.boolInjections ?? new BoolInjection[0]);
                foreach (var kv in bools)
                {
                    if (Has(list.ConvertAll(x => x.name), kv.Key)) continue;
                    list.Add(new BoolInjection { name = kv.Key, value = kv.Value });
                }
                behaviour.boolInjections = list.ToArray();
            }

            if (ints != null)
            {
                var list = new List<IntInjection>(behaviour.intInjections ?? new IntInjection[0]);
                foreach (var kv in ints)
                {
                    if (Has(list.ConvertAll(x => x.name), kv.Key)) continue;
                    list.Add(new IntInjection { name = kv.Key, value = kv.Value });
                }
                behaviour.intInjections = list.ToArray();
            }

            if (vectors != null)
            {
                var list = new List<Vector3Injection>(behaviour.vector3Injections ?? new Vector3Injection[0]);
                foreach (var kv in vectors)
                {
                    if (Has(list.ConvertAll(x => x.name), kv.Key)) continue;
                    list.Add(new Vector3Injection { name = kv.Key, value = kv.Value });
                }
                behaviour.vector3Injections = list.ToArray();
            }
        }

        static bool Has(List<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.Ordinal)) return true;
            return false;
        }

        // ── Utilities ───────────────────────────────────────────────────

        static string AbsolutePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            assetPath = assetPath.Replace('\\', '/');
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            return Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
        }

        static void Report(ConversionResult r, string message)
        {
            if (r == null) return;
            r.Added(message);
        }
    }
}
#endif
