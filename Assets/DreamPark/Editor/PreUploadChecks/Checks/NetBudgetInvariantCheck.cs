#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using UnityEditor;

namespace DreamPark.PreUploadChecks.Checks
{
    // DreamBoxClient.DesignBudget must stay at or below what a peer relay enforces.
    //
    // WHY THIS IS A BUILD GATE AND NOT A RUNTIME LOG
    //
    // The thing being protected against is somebody editing a constant in a .cs
    // file. That should fail before it ships, not print a line on a headset in a
    // park where nobody is reading the console. The first version of this lived in
    // a [RuntimeInitializeOnLoadMethod] on DreamBoxClient, which was the wrong
    // shape twice over: it fired at the one moment nobody could act on it, and it
    // fired once per session forever rather than once per edit.
    //
    // (It used Debug.LogError, so it would at least have survived into a release
    // player — Debug.Assert would not have: UnityEngine.Debug.Assert is
    // [Conditional("UNITY_ASSERTIONS")] and is stripped. But surviving into a
    // release player is not the same as being useful there.)
    //
    // WHY THE INVARIANT MATTERS
    //
    // DesignBudget is a content contract: the send rate every park is told to
    // design against, on every host type, forever. PeerRelayServer's cap is a
    // per-device protection value, sitting beside MaxPeers, whose own comment
    // invites tuning it per venue. Raising the cap above the budget is fine and
    // expected. Dropping it below means every shipped park is now budgeting for
    // more than a peer host will accept — silently, and only visible as desync
    // after a reelection.
    public sealed class NetBudgetInvariantCheck : IPreUploadCheck
    {
        public const string CheckId = "net-budget-invariant";

        const string ClientPath = "Assets/DreamPark/Scripts/Features/Net/DreamBoxClient.cs";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Network send budget vs relay cap"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Blocking; } }

        // Two integer constants. There is no cheaper check in the suite.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "DesignBudget is the send rate every park is told to design against. If a relay's "
                     + "cap is lowered below it, every shipped park is budgeting for more than the relay "
                     + "will accept — which surfaces only as desync after a host migration.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            int budget = DreamBoxClient.DesignBudget;
            int cap = PeerRelayServer.MaxMessagesPerPeerPerSecond;
            if (budget <= cap) return CheckResult.Clean(CheckId);

            var finding = new Finding
            {
                checkId = CheckId,
                severity = CheckSeverity.Blocking,
                assetPath = ClientPath,
                assetGuid = AssetDatabase.AssetPathToGUID(ClientPath),
                title = string.Format(
                    "DesignBudget ({0}/s) is above PeerRelayServer's cap ({1}/s)", budget, cap),
                detail = string.Format(
                    "DreamBoxClient.DesignBudget is the number content is told to design against, and it "
                  + "must stay at or below the lowest cap any host enforces. It is currently {0}/s while "
                  + "PeerRelayServer.MaxMessagesPerPeerPerSecond is {1}/s, so every park budgeting to the "
                  + "published figure will be truncated by a peer host with no error to the sender.\n\n"
                  + "Either raise the relay cap back to at least {0}, or lower DesignBudget — the second "
                  + "is a breaking change for every park that already budgeted against it, and should be "
                  + "announced rather than merged.", budget, cap),
            };
            finding.fixes.Add(FixAction.Navigate("Select DreamBoxClient.cs", () =>
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ClientPath);
                if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }
            }));

            return CheckResult.From(CheckId, new List<Finding> { finding });
        }
    }
}
#endif
