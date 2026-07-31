// ─────────────────────────────────────────────────────────────────────
//  LuaSurfaceGate.cs — SDK-synced editor tooling
//
//  Runs the two Lua checks and decides what, if anything, interrupts a human.
//
//  WHAT EARNS A DIALOG, AND WHY MOST OF THIS DOES NOT
//
//  A modal with an "Upload anyway" button is a scarce resource. Spend it on a
//  finding that is wrong often enough to be dismissed and it stops meaning
//  anything — people learn the reflex, and then the one that mattered gets
//  clicked through too. That already happened once here: the first version of
//  this gate showed a wall of unregistered types, most of them nonsense, and
//  the dev clicked past it.
//
//    BLOCKED (sandbox-denied type)     → hard stop, dialog.
//        These do not degrade, they THROW when content runs at a venue.
//        No judgement to make. A file that is genuinely an authoring tool
//        opts out with `-- @editor-only`.
//
//    CODEGEN DRIFT (config has a type   → hard stop on BUILD, console on upload.
//    with no wrapper in Gen/)              Always true, always actionable. This
//        is the check that would have caught the July 2026 Zombiez bug, where
//        codegen had never succeeded and every type was reflection-only.
//        Blocks a player build because shipping an APK with stale wrappers
//        means testing against a runtime that is not the one you ship. Does
//        not block an UPLOAD, because content carries assets and Lua text —
//        wrappers ride the app, so a creator's stale Gen/ cannot reach a guest.
//
//    UNREGISTERED (no wrapper for a     → console warning only. No dialog.
//    type Lua names)                       Deliberately demoted.
//        The failure this hints at is a call signature AOT cannot fake — a
//        struct passed by `out`, or a runtime-instantiated generic. But it
//        fires on "type has no wrapper", and those two sets barely overlap:
//        reflection handles ordinary member access on device perfectly well.
//        So nearly every finding is "maybe nothing", which is exactly the
//        shape that trains people to dismiss dialogs.
//
//        It stays as a Console warning because it is genuinely useful when
//        someone is already debugging. And it is now largely redundant at
//        runtime: the SDK defines NOT_GEN_WARNING, so a creator's own headset
//        build NAMES every type that fell back to reflection — observed rather
//        than predicted, which beats a static guess.
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
    /// Never throws: a broken check must not be able to block shipping.
    /// </summary>
    public static bool PassesPreUploadCheck()
    {
        // ── Codegen drift: report, never block an upload ──────────────
        try
        {
            var codegen = LuaCodegenIntegrity.Check();
            if (!codegen.IsClean)
                Debug.LogWarning(codegen.report +
                    "\n   (Not blocking this upload — wrappers ship inside the app, not with content.\n" +
                    "    It DOES mean your local test build is missing them.)");
        }
        catch (Exception e) { Debug.LogWarning("[LuaGate] Codegen check failed: " + e.Message); }

        // ── Surface scan ──────────────────────────────────────────────
        LuaSurfaceScanner.ScanResult result;
        try { result = LuaSurfaceScanner.Analyze(); }
        catch (Exception e)
        {
            Debug.LogWarning("[LuaGate] Scan failed, allowing upload: " + e.Message);
            return true;
        }

        if (result == null || result.IsClean) return true;

        if (result.HasUnregistered)
            Debug.LogWarning(result.report);

        if (!result.HasBlocked) return true;

        Debug.LogError(result.report);
        EditorUtility.DisplayDialog(
            "Upload blocked — sandboxed API in Lua",
            "Your content calls types the production sandbox denies. They work here in " +
            "the Editor and THROW at a venue:\n\n" +
            LuaSurfaceScanner.ScanResult.Summarize(result.blocked) +
            "\nIf one of those files is an authoring tool that never runs at a venue, add\n" +
            "    -- " + LuaSurfaceScanner.EditorOnlyMarker + "\n" +
            "to it, or move it under an Editor/ folder.\n\n" +
            "Full detail is in the Console.",
            "OK");
        return false;
    }

    /// <summary>
    /// Player builds. Sandbox-denied types and codegen drift both fail the build;
    /// unregistered types are a console warning.
    /// </summary>
    public class BuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            // Codegen drift first: it is the cheapest to check and the most
            // consequential to ship. An APK built against stale wrappers behaves
            // differently from the app the content will actually run in.
            try
            {
                var codegen = LuaCodegenIntegrity.Check();
                if (!codegen.IsClean)
                {
                    Debug.LogError(codegen.report);
                    throw new BuildFailedException(
                        "XLua codegen is out of date — " +
                        (codegen.genFolderMissing
                            ? "Gen/ does not exist."
                            : codegen.missing.Count + " configured type(s) have no generated wrapper.") +
                        " Run DreamPark ▸ Troubleshooting ▸ Generate XLua Code. See the Console.");
                }
            }
            catch (BuildFailedException) { throw; }
            catch (Exception e) { Debug.LogWarning("[LuaGate] Codegen check failed: " + e.Message); }

            LuaSurfaceScanner.ScanResult result;
            try { result = LuaSurfaceScanner.Analyze(); }
            catch (Exception e)
            {
                Debug.LogWarning("[LuaGate] Scan failed, continuing build: " + e.Message);
                return;
            }
            if (result == null || result.IsClean) return;

            if (result.HasUnregistered) Debug.LogWarning(result.report);

            if (result.HasBlocked)
            {
                Debug.LogError(result.report);
                throw new BuildFailedException(
                    "Lua calls types the production sandbox denies: " +
                    string.Join(", ", result.blocked.ConvertAll(f => f.type)) +
                    ". See the Console for detail.");
            }
        }
    }
}
#endif
