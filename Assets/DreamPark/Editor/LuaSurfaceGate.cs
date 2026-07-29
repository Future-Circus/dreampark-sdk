// ─────────────────────────────────────────────────────────────────────
//  LuaSurfaceGate.cs — SDK-synced editor tooling
//
//  The thing that actually RUNS LuaSurfaceScanner.
//
//  The scanner has existed for a while and was wired to nothing — not the
//  content upload, not the player build, not PIPELINE.md. It was a good
//  detector attached to no trigger, which is the same as not having it: a
//  creator who never opened DreamPark ▸ Troubleshooting shipped blind, and
//  the failure it catches (a Unity type with no XLua wrapper) is invisible in
//  the Editor, silent on device inside a pcall, and unfixable by republishing
//  content because the wrappers are AOT code compiled into the app.
//
//  Two triggers, two different severities:
//
//    BLOCKED (sandbox-denied types)   → hard stop. These do not degrade, they
//                                       throw the moment the content runs at a
//                                       venue. There is no "ship it anyway"
//                                       reading of System.IO in creator Lua.
//
//    UNREGISTERED (no XLua wrapper)   → warn, and let the human decide. Most
//                                       reflection fallbacks do work; the ones
//                                       that don't (a struct passed by `out`)
//                                       are catastrophic and silent. Blocking
//                                       every upload on the maybe-case would
//                                       train people to click through, which is
//                                       worse than a dialog that means
//                                       something.
//
//  Editor-only.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class LuaSurfaceGate
{
    /// <summary>
    /// Called from the content upload entry point. Returns false to abort.
    /// Never throws: a broken scan must not be able to block shipping.
    /// </summary>
    public static bool PassesPreUploadCheck()
    {
        LuaSurfaceScanner.ScanResult result;
        try { result = LuaSurfaceScanner.Analyze(); }
        catch (Exception e)
        {
            Debug.LogWarning("[LuaGate] Scan failed, allowing upload: " + e.Message);
            return true;
        }

        if (result == null || result.IsClean) return true;

        if (result.HasBlocked)
        {
            Debug.LogError(result.report);
            EditorUtility.DisplayDialog(
                "Upload blocked — sandboxed API in Lua",
                "Your content calls types the production sandbox denies, so they will THROW " +
                "at a venue even though they work here in the Editor:\n\n" +
                result.BlockedSummary +
                "\n\nFull detail is in the Console. Remove these calls and upload again.",
                "OK");
            return false;
        }

        Debug.LogWarning(result.report);
        bool proceed = EditorUtility.DisplayDialog(
            "Unregistered Unity types in Lua",
            "These types have no XLua wrapper compiled into the app:\n\n" +
            result.UnregisteredSummary +
            "\n\nThey work in the Editor (Mono reflection) and usually work on device too — " +
            "but anything that passes a struct by `out` fails silently on a headset, inside " +
            "your pcall, with nothing in the log.\n\n" +
            "Because wrappers ship inside the app and your content ships over the air, fixing " +
            "this later needs an app release, not a re-upload.\n\n" +
            "Full detail is in the Console.",
            "Upload anyway",
            "Cancel and fix");

        if (!proceed)
            Debug.Log("[LuaGate] Upload cancelled. Add the types to DreamParkLuaConfig.LuaCallCSharp, " +
                      "then run DreamPark ▸ Troubleshooting ▸ Generate XLua Code.");
        return proceed;
    }

    /// <summary>
    /// Player builds get the same check. Blocked types fail the build outright;
    /// unregistered ones are a console warning, because a build is often a
    /// deliberate mid-iteration test and stopping it would be obnoxious.
    /// </summary>
    public class BuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            LuaSurfaceScanner.ScanResult result;
            try { result = LuaSurfaceScanner.Analyze(); }
            catch (Exception e)
            {
                Debug.LogWarning("[LuaGate] Scan failed, continuing build: " + e.Message);
                return;
            }
            if (result == null || result.IsClean) return;

            if (result.HasBlocked)
            {
                Debug.LogError(result.report);
                throw new BuildFailedException(
                    "Lua calls types the production sandbox denies: " + result.BlockedSummary +
                    ". See the Console for detail.");
            }

            Debug.LogWarning(result.report);
        }
    }
}
#endif
