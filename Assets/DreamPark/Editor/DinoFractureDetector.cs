#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// Auto-toggles the DINOFRACTURE scripting define symbol based on whether
    /// the DinoFracture asset is present in the project.
    ///
    /// DinoFracture is an OPTIONAL Asset Store dependency used by EasyShatter
    /// (see Assets/DreamPark/Scripts/Features/5. EasyEvent/EasyShatter.cs). The
    /// SDK does not bundle DinoFracture for licensing reasons. This detector
    /// removes the manual setup step:
    ///
    ///   - Import DinoFracture from the Unity Asset Store → on next assembly
    ///     reload, this script detects DinoFracture.FractureGeometry, adds
    ///     DINOFRACTURE to the active build target's scripting defines, and
    ///     EasyShatter becomes functional automatically.
    ///   - Remove DinoFracture from the project → this script removes
    ///     DINOFRACTURE from the defines, and EasyShatter falls back to its
    ///     no-op stub. Prefabs and scenes that reference EasyShatter remain
    ///     valid; firing the event logs a warning instead of shattering.
    ///
    /// This script lives in the Editor folder so it never compiles into player
    /// builds. It runs once per editor session at InitializeOnLoad time.
    /// </summary>
    [InitializeOnLoad]
    internal static class DinoFractureDetector
    {
        private const string DefineSymbol = "DINOFRACTURE";

        // Type DinoFracture exposes that we use as a presence sentinel. If this
        // type resolves at editor load, DinoFracture is installed.
        private const string SentinelTypeName = "DinoFracture.FractureGeometry";

        static DinoFractureDetector()
        {
            // Defer to the next editor tick so all assemblies are loaded before
            // we probe — InitializeOnLoad fires before some assemblies finish
            // loading, which can cause false negatives on a fresh import.
            EditorApplication.delayCall += SyncDefine;
        }

        private static void SyncDefine()
        {
            bool present = IsDinoFracturePresent();

            // EVERY build target, not just the selected one.
            //
            // This used to sync only EditorUserBuildSettings.selectedBuildTargetGroup,
            // which leaves the define stranded on every other platform the moment
            // you switch targets. Remove DinoFracture while Android is selected and
            // Standalone keeps DINOFRACTURE forever: nothing ever revisits it,
            // because the detector only ever looks at whatever is selected now.
            //
            // That is invisible in the editor and fatal at upload. The Content
            // Uploader cross-builds StandaloneOSX regardless of the selected
            // target, so it compiles player scripts against the Standalone define
            // set, hits `using DinoFracture;` for a type that is not in the
            // project, and the whole Addressables build fails with a CS0246 that
            // points at an SDK file the park never touched.
            foreach (var namedTarget in AllNamedBuildTargets())
            {
                try
                {
                    string defines = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
                    var defineList = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(s => s.Trim())
                                            .Where(s => s.Length > 0)
                                            .ToList();

                    bool currentlyDefined = defineList.Contains(DefineSymbol);

                    if (present && !currentlyDefined)
                    {
                        defineList.Add(DefineSymbol);
                        PlayerSettings.SetScriptingDefineSymbols(namedTarget, string.Join(";", defineList));
                        Debug.Log($"[DinoFractureDetector] DinoFracture detected — added '{DefineSymbol}' to scripting defines for {namedTarget.TargetName}. EasyShatter is now active.");
                    }
                    else if (!present && currentlyDefined)
                    {
                        defineList.Remove(DefineSymbol);
                        PlayerSettings.SetScriptingDefineSymbols(namedTarget, string.Join(";", defineList));
                        Debug.Log($"[DinoFractureDetector] DinoFracture not found — removed '{DefineSymbol}' from scripting defines for {namedTarget.TargetName}. EasyShatter falls back to stub.");
                    }
                }
                catch (Exception e)
                {
                    // A platform module that is not installed throws on read/write.
                    // Skipping it is correct — you cannot build for it anyway.
                    if (debugTargets)
                        Debug.Log($"[DinoFractureDetector] skipped {namedTarget.TargetName}: {e.Message}");
                }
            }
        }

        // Set true to see which targets were skipped for want of an installed module.
        private const bool debugTargets = false;

        /// <summary>
        /// Every NamedBuildTarget the installed editor exposes, read off the type's
        /// own static fields rather than hard-coded — a hard-coded list silently
        /// stops covering whatever platform Unity adds next, which is the same
        /// failure mode this method exists to fix.
        /// </summary>
        private static IEnumerable<NamedBuildTarget> AllNamedBuildTargets()
        {
            var fields = typeof(NamedBuildTarget).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            foreach (var f in fields)
            {
                if (f.FieldType != typeof(NamedBuildTarget)) continue;

                NamedBuildTarget t;
                try { t = (NamedBuildTarget)f.GetValue(null); }
                catch { continue; }

                // NamedBuildTarget.Unknown has an empty name and is not settable.
                if (string.IsNullOrEmpty(t.TargetName)) continue;

                yield return t;
            }
        }

        private static bool IsDinoFracturePresent()
        {
            // Walk loaded assemblies and look for the sentinel type. Works
            // regardless of which assembly DinoFracture lands in (Assembly-CSharp
            // for loose-script imports, a custom asmdef for packaged versions).
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetType(SentinelTypeName, throwOnError: false, ignoreCase: false) != null)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Some dynamic/transient assemblies throw on GetType; ignore them.
                }
            }
            return false;
        }
    }
}
#endif
