#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;

namespace DreamPark.PreUploadChecks
{
    // Shared vocabulary for the pre-upload validation suite.
    //
    // POLICY, inherited from LuaSurfaceGate:
    //
    //   "A modal with an 'Upload anyway' button is a scarce resource. Spend it on a
    //    finding that is wrong often enough to be dismissed and it stops meaning
    //    anything… That already happened once here."
    //
    // Hence three severities rather than two, and hence MetaOcclusion's third
    // "Unknown" state, which is reported as a Warning and never as a failure. A check
    // that cannot tell must not block.
    //
    // Every check also runs inside its own try/catch (see PreUploadCheckRunner). A
    // check that throws is Errored and contributes ZERO blocking findings: a broken
    // check must never be able to stop someone shipping.

    public enum CheckSeverity
    {
        Info = 0,
        Warning = 1,
        Blocking = 2,
    }

    public enum CheckOutcome
    {
        Clean,
        HasFindings,
        Errored,
        Skipped,
    }

    public enum ContentRootKindPublic
    {
        Attraction,
        Prop,
        Player,
    }

    // A public mirror of ContentUploaderPanel's private nested ContentRootEntry, so
    // the checks can be written outside that class.
    public sealed class ContentRootInfo
    {
        public string assetPath;
        public string name;                 // Path.GetFileNameWithoutExtension(assetPath)
        public string guid;
        public ContentRootKindPublic kind;

        public string KindLabel
        {
            get
            {
                switch (kind)
                {
                    case ContentRootKindPublic.Attraction: return "Attraction";
                    case ContentRootKindPublic.Prop: return "Prop";
                    default: return "Player";
                }
            }
        }
    }

    // A single actionable thing the dev can do about a finding.
    public sealed class FixAction
    {
        public string label;
        public string tooltip;
        public string confirmTitle;
        public string confirmMessage;       // null → no confirmation dialog

        // True when this action is meant to CHANGE something, so a false return really
        // is a failure worth logging. Navigation actions ("Select", "Open scene")
        // resolve nothing by design — counting those as failures prints a warning
        // every time someone clicks Select, which trains people to ignore the log this
        // suite writes to.
        public bool resolvesFinding = true;

        // Returns true if the finding should be considered resolved.
        public Func<bool> run;

        public FixAction(string label, Func<bool> run)
        {
            this.label = label;
            this.run = run;
        }

        public static FixAction Navigate(string label, Action go)
        {
            return new FixAction(label, () => { go(); return false; }) { resolvesFinding = false };
        }
    }

    public sealed class Finding
    {
        public string checkId;
        public CheckSeverity severity;

        // Identity. assetGuid is the durable key — it survives move and rename, which
        // matters because the ignore list is meant to outlive refactors. assetPath is
        // carried alongside for display, for matching against the uploader's tiles
        // (which key by path), and for diff legibility in the ignore file.
        public string assetGuid;
        public string assetPath;

        // Optional sub-key so one asset can carry several independently-ignorable
        // findings from the same check — e.g. two different materials missing
        // occlusion, or the same prefab overridden in two different scenes.
        public string subKey;

        public string title;                // one line, the list row
        public string detail;               // paragraph; also the badge tooltip
        public List<FixAction> fixes = new List<FixAction>();

        // Set by the runner from the ignore store. Ignored findings never block and
        // never badge, but stay visible in the popup's "Ignored" section.
        public bool isIgnored;

        public string IgnoreKey
        {
            get { return checkId + "|" + (assetGuid ?? "") + "|" + (subKey ?? ""); }
        }
    }

    public sealed class CheckResult
    {
        public string checkId;
        public CheckOutcome outcome;
        public List<Finding> findings = new List<Finding>();
        public string errorMessage;
        public string skipReason;

        public static CheckResult Clean(string checkId)
        {
            return new CheckResult { checkId = checkId, outcome = CheckOutcome.Clean };
        }

        public static CheckResult Errored(string checkId, string message)
        {
            return new CheckResult
            {
                checkId = checkId,
                outcome = CheckOutcome.Errored,
                errorMessage = message,
            };
        }

        public static CheckResult Skipped(string checkId, string reason)
        {
            return new CheckResult
            {
                checkId = checkId,
                outcome = CheckOutcome.Skipped,
                skipReason = reason,
            };
        }

        public static CheckResult From(string checkId, List<Finding> findings)
        {
            if (findings == null || findings.Count == 0) return Clean(checkId);
            return new CheckResult
            {
                checkId = checkId,
                outcome = CheckOutcome.HasFindings,
                findings = findings,
            };
        }
    }

    public sealed class PreUploadCheckContext
    {
        public string contentId;
        public string contentRoot;                      // "Assets/Content/{contentId}"
        public IReadOnlyList<ContentRootInfo> roots;

        // Mirrors MaterialUsageGraph.Build's progress signature so the two feel the
        // same at the call site.
        public Action<float, string> onProgress;

        // True when the upload path is about to compile — meaning open scenes have
        // just been saved and it is safe to read scene YAML / open scenes. False for
        // the advisory scan, where checks that need saved scenes should Skip rather
        // than read stale state.
        public bool scenesAreSaved;

        public void Progress(float t, string message)
        {
            if (onProgress != null) onProgress(t, message);
        }
    }

    public interface IPreUploadCheck
    {
        string Id { get; }
        string DisplayName { get; }
        string Rationale { get; }               // section subtitle in the popup
        CheckSeverity DefaultSeverity { get; }

        // True if this check is cheap enough to run every time the Content Uploader
        // panel is opened. Expensive checks run only at the upload gate and on an
        // explicit "Run" button.
        bool RunsInAdvisoryScan { get; }

        CheckResult Run(PreUploadCheckContext ctx);
    }

    public sealed class PreUploadReport
    {
        public string contentId;
        public List<CheckResult> results = new List<CheckResult>();

        public IEnumerable<Finding> AllFindings
        {
            get { return results.SelectMany(r => r.findings ?? new List<Finding>()); }
        }

        public IEnumerable<Finding> ActiveFindings
        {
            get { return AllFindings.Where(f => !f.isIgnored); }
        }

        public int BlockingCount
        {
            get { return ActiveFindings.Count(f => f.severity == CheckSeverity.Blocking); }
        }

        public int WarningCount
        {
            get { return ActiveFindings.Count(f => f.severity == CheckSeverity.Warning); }
        }

        public int IgnoredCount
        {
            get { return AllFindings.Count(f => f.isIgnored); }
        }

        public bool HasBlocking { get { return BlockingCount > 0; } }

        // The gate only interrupts for Blocking or Warning. Info-level findings are
        // reference material, not a reason to put a window in someone's way — a clean
        // project must upload with exactly the friction it had before this suite
        // existed, or people learn to click through.
        public bool HasActionableFindings
        {
            get
            {
                return ActiveFindings.Any(f => f.severity == CheckSeverity.Warning
                                            || f.severity == CheckSeverity.Blocking);
            }
        }

        public CheckSeverity WorstSeverity
        {
            get
            {
                var active = ActiveFindings.ToList();
                if (active.Count == 0) return CheckSeverity.Info;
                return active.Max(f => f.severity);
            }
        }
    }
}
#endif
