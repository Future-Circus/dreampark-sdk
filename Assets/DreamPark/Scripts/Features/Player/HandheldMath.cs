using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// The hand-held mounting convention (Assets/DreamPark/Docs/HANDHELD.md) as plain maths.
    /// No component, no state: builders call it at edit time (<see cref="Place"/>), the engine
    /// calls it behind Lua <c>dp.hold</c> (<see cref="Handhelds"/>), and the pre-upload check and
    /// EditMode tests call the validators. It is a port of the creaturez <c>HandheldKit</c>
    /// (Tools/Builders/Handheld/HandheldKit.cs: FindGrip / ExpectedGripPose / MountPose / Place /
    /// ValidateGO / ValidateRigGO) and of <c>handheld_mount.lua.txt</c> /
    /// <c>two_hand_aim.lua.txt</c>, with identical numbers (0 mm / 0° parity).
    ///
    /// THE FRAME. HandTracker and the RemoteRig hand nodes copy OVRCameraRig's
    /// Left/RightHandAnchor = the OpenXR WRIST joint. Same axes on both hands: +Z wrist →
    /// fingers, +Y back of the hand (palm −Y), thumb −X on the right hand / +X on the left.
    /// Targets (raw wrist frame, m): fist centre (∓0.007, −0.033, 0.081) (Meta's power-grip
    /// axis under the middle finger), palm surface (0, −0.022, 0.064).
    ///
    /// THE PROP. Root = the object (scale 1, no hand rotation). Direct-child markers:
    /// <c>Grip</c> (lands on the fist centre; +Z = where the fingers point, +Y = thumb side) or
    /// <c>PalmGrip</c> (lands on the palm surface); optional <c>GripLeft</c> / <c>PalmGripLeft</c>
    /// for the left hand; optional <c>Grip2</c> (off-hand fist of a two-handed prop);
    /// <c>Muzzle</c> (+Z = shot direction); the art lives on <c>Model</c>. The hold archetype
    /// (pistol, wand, sword, palm …) is DATA in the Grip pose (HANDHELD.md §5).
    ///
    /// THE MOUNT (HANDHELD.md §5 "dp.hold" contract). Marker: left uses GripLeft, PalmGripLeft,
    /// Grip, PalmGrip; right uses Grip, PalmGrip. <c>rot = Euler(0,0,left?−90:90) ×
    /// Inverse(marker.localRotation)</c>, <c>pos = target − rot × marker.localPosition</c>, in the
    /// hand node's raw-anchor frame; a HandTracker with flipVisual driven by the LEFT anchor
    /// pre-applies Inverse(flipRot) and subtracts flipPos. A pure rotation — never a −1 scale.
    /// </summary>
    public static class HandheldMath
    {
        public const string GripName = "Grip";
        public const string PalmGripName = "PalmGrip";
        public const string GripLeftName = "GripLeft";
        public const string PalmGripLeftName = "PalmGripLeft";
        public const string Grip2Name = "Grip2";
        public const string MuzzleName = "Muzzle";
        public const string ModelName = "Model";

        /// <summary>Every grip marker name (the first four identify a prop).</summary>
        public static readonly string[] GripNames = { GripName, PalmGripName, GripLeftName, PalmGripLeftName, Grip2Name };

        /// <summary>Fist centre on the RIGHT hand, raw wrist frame, metres.</summary>
        public static readonly Vector3 FistCentreRight = new Vector3(-0.007f, -0.033f, 0.081f);
        /// <summary>Open-palm contact point, both hands, raw wrist frame, metres.</summary>
        public static readonly Vector3 PalmSurface = new Vector3(0f, -0.022f, 0.064f);

        public const float GripTolerance = 0.02f;      // m, marker vs target (ValidateRig)
        public const float AngleTolerance = 10f;       // deg, marker vs expected rotation
        public const float RemoteTolerance = 0.005f;   // m, remote copy vs local copy
        public const float RemoteAngleTolerance = 2f;  // deg

        public static Vector3 FistCentre(bool left)
            => left ? new Vector3(-FistCentreRight.x, FistCentreRight.y, FistCentreRight.z) : FistCentreRight;

        /// <summary>Marker orientation in the raw wrist frame: marker +Z → fingers, +Y → thumb side.</summary>
        public static Quaternion HandFrame(bool left) => Quaternion.Euler(0f, 0f, left ? -90f : 90f);

        /// <summary>
        /// The frame the Editor's simulated hands use: "device" since Oct 2026 (the OpenXR wrist,
        /// <see cref="EditorAnchorRotation"/>). Before, both anchors copied the camera rotation
        /// ("camera"). Lua: dp.editor_hand_frame(). Content that compensated for the old Editor
        /// frame (DreamSports ds_sportsrig) should check this and drop its compensation.
        /// </summary>
        public const string EditorHandFrame = "device";

        /// <summary>"left" / "l" / "L" → true.</summary>
        public static bool IsLeft(string side) => !string.IsNullOrEmpty(side) && char.ToLowerInvariant(side[0]) == 'l';

        /// <summary>
        /// Rotation the Editor's simulated hand anchors (Simulator, PlaytestAvatar) use so the
        /// Editor matches the headset: fingers along the look direction, thumb up, palm inward
        /// (a pistol grip pointing where you look). An identity Grip mounted under it ends up
        /// exactly at <paramref name="head"/>'s rotation.
        /// </summary>
        public static Quaternion EditorAnchorRotation(Quaternion head, bool left)
            => head * Quaternion.Inverse(HandFrame(left));

        // ── Markers ─────────────────────────────────────────────────────

        /// <summary>
        /// The marker a hand uses. palm = it is a PalmGrip*. Priority, documented and stable:
        /// <list type="number">
        /// <item>by NAME first — left: GripLeft, PalmGripLeft, Grip, PalmGrip; right: Grip, PalmGrip;</item>
        /// <item>for each name, a DIRECT child (the HANDHELD.md convention) wins; otherwise the
        ///   shallowest descendant with that name (breadth-first, sibling order), so props built as
        ///   <c>prop/Body/Grip</c> or <c>prop/Gun/{Grip,Muzzle}</c> still mount.</item>
        /// </list>
        /// A nested marker's pose is taken relative to the prop root (<see cref="MarkerPose"/>).
        /// </summary>
        public static Transform FindGrip(Transform prop, bool left, out bool palm)
        {
            palm = false;
            if (prop == null) return null;
            var names = left ? new[] { GripLeftName, PalmGripLeftName, GripName, PalmGripName } : new[] { GripName, PalmGripName };
            foreach (var n in names)
            {
                var t = FindMarker(prop, n);
                if (t != null) { palm = n.StartsWith("Palm"); return t; }
            }
            return null;
        }

        /// <summary>A marker by name: the direct child, else the shallowest descendant (breadth-first).</summary>
        public static Transform FindMarker(Transform prop, string name)
        {
            if (prop == null) return null;
            var direct = prop.Find(name);
            if (direct != null) return direct;
            var queue = new Queue<Transform>();
            for (int i = 0; i < prop.childCount; i++) queue.Enqueue(prop.GetChild(i));
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (t.name == name) return t;
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            return null;
        }

        /// <summary>
        /// Marker pose in prop-root space: localPosition/localRotation for a direct child
        /// (bit-identical to HandheldKit and handheld_mount.lua), the composed pose for a nested one.
        /// </summary>
        public static void MarkerPose(Transform prop, Transform marker, out Vector3 pos, out Quaternion rot)
        {
            if (marker == null) { pos = Vector3.zero; rot = Quaternion.identity; return; }
            if (marker.parent == prop) { pos = marker.localPosition; rot = marker.localRotation; return; }
            pos = prop.InverseTransformPoint(marker.position);
            rot = Quaternion.Inverse(prop.rotation) * marker.rotation;
        }

        /// <summary>The prop's Muzzle (direct child, else the shallowest descendant), or null.</summary>
        public static Transform FindMuzzle(Transform prop) => FindMarker(prop, MuzzleName);

        /// <summary>dp.muzzle: Muzzle position/forward (the root's when there is no Muzzle). Read it every shot.</summary>
        public static void Muzzle(Transform prop, out Vector3 pos, out Vector3 forward)
        {
            Transform m = FindMuzzle(prop);
            Transform t = m != null ? m : prop;
            pos = t != null ? t.position : Vector3.zero;
            forward = t != null ? t.forward : Vector3.forward;
        }

        /// <summary>Is this a hand-held prop (a Muzzle or one of the four grip markers as a direct child)?</summary>
        public static bool IsProp(Transform t)
            => t != null && t.name != ModelName && (t.Find(MuzzleName) != null || GripNames.Take(4).Any(n => t.Find(n) != null));

        // ── The mount ───────────────────────────────────────────────────

        /// <summary>
        /// Marker pose expected in a hand node's frame. <paramref name="flip"/>: the node is a
        /// HandTracker with flipVisual (applied only on the LEFT side: the tracker frame is
        /// raw × TRS(flipPos, Euler(flipRot)) while the left anchor drives it).
        /// </summary>
        public static void ExpectedGripPose(bool left, bool flip, Vector3 flipRot, Vector3 flipPos,
                                            out Vector3 pos, out Quaternion rot, bool palm = false, Vector3? targetRight = null)
        {
            // targetRight: a per-prop calibrated target given for the RIGHT hand (x mirrors on the left),
            // e.g. a rig tuned on device to another point than Meta's fist centre.
            pos = targetRight.HasValue ? (left ? new Vector3(-targetRight.Value.x, targetRight.Value.y, targetRight.Value.z) : targetRight.Value)
                : palm ? PalmSurface : FistCentre(left);
            rot = HandFrame(left);
            if (left && flip)
            {
                var fi = Quaternion.Inverse(Quaternion.Euler(flipRot));
                pos = fi * (pos - flipPos);
                rot = fi * rot;
            }
        }

        /// <summary>The prop-root pose in hand-node space that puts its marker on the target.</summary>
        public static void MountPose(bool left, Vector3 gripLocalPos, Quaternion gripLocalRot, out Vector3 pos, out Quaternion rot,
                                     bool flip = false, Vector3? flipRot = null, Vector3? flipPos = null, bool palm = false,
                                     Vector3? targetRight = null)
        {
            ExpectedGripPose(left, flip, flipRot ?? new Vector3(180f, 180f, 0f), flipPos ?? Vector3.zero, out var gp, out var gr, palm, targetRight);
            rot = gr * Quaternion.Inverse(gripLocalRot);
            pos = gp - rot * gripLocalPos;
        }

        /// <summary>
        /// Pose <paramref name="prop"/> for <paramref name="handNode"/> (HANDHELD.md §7). Works
        /// with the prop under any child of the node (a jointed HandTouch). Does not reparent.
        /// </summary>
        public static void Place(Transform handNode, Transform prop, bool left, bool flip = false, Vector3? flipRot = null, Vector3? flipPos = null,
                                 Vector3? targetRight = null)
        {
            if (handNode == null || prop == null) return;
            var grip = FindGrip(prop, left, out bool palm);
            MarkerPose(prop, grip, out var glp, out var glr);
            MountPose(left, glp, glr, out var p, out var r, flip, flipRot, flipPos, palm, targetRight);
            prop.SetPositionAndRotation(handNode.TransformPoint(p), handNode.rotation * r);
        }

        /// <summary>Place for a node that may carry a HandTracker (its flipVisual is honoured on the left).</summary>
        public static void Mount(Transform prop, Transform handNode, bool left, HandTracker tracker = null)
        {
            bool flip = tracker != null && tracker.flipVisual;
            Place(handNode, prop, left, flip,
                  tracker != null ? tracker.flipVisualRotation : (Vector3?)null,
                  tracker != null ? tracker.flipVisualPosition : (Vector3?)null);
        }

        /// <summary>
        /// How far a mounted prop is from the device pose (ValidateRig's measure): the marker's
        /// distance from its target (m) and angle from the expected marker rotation (°), in the
        /// hand node's frame, flipVisual included. A node with a tracker decides the flip itself.
        /// </summary>
        public static void MeasureMount(Transform prop, Transform node, bool left, HandTracker tracker,
                                        out float distance, out float angle, Vector3? targetRight = null)
        {
            var grip = FindGrip(prop, left, out bool palm);
            Transform g = grip != null ? grip : prop;
            Vector3 gp = node.InverseTransformPoint(g.position);
            Quaternion gr = Quaternion.Inverse(node.rotation) * g.rotation;
            bool flip = tracker != null && tracker.flipVisual;
            ExpectedGripPose(left, flip, tracker ? tracker.flipVisualRotation : Vector3.zero, tracker ? tracker.flipVisualPosition : Vector3.zero,
                             out var ep, out var er, palm, targetRight);
            distance = (gp - ep).magnitude;
            angle = Quaternion.Angle(gr, er);
        }

        /// <summary>
        /// two_hand_aim.lua.txt, exactly: pivot the prop about the main fist so Grip → Grip2
        /// points at the off fist, keeping the roll from the main hand's thumb. False when it
        /// needs the one-hand pose instead (missing markers, hands closer than 5 cm).
        /// </summary>
        public static bool TwoHandAim(Transform prop, Transform mainNode, bool mainLeft, Transform offNode, bool offLeft)
        {
            if (prop == null || mainNode == null || offNode == null) return false;
            var gripT = FindMarker(prop, GripName);
            var grip2T = FindMarker(prop, Grip2Name);
            if (gripT == null || grip2T == null) return false;
            MarkerPose(prop, gripT, out var gripPos, out var gripRot);
            MarkerPose(prop, grip2T, out var grip2Pos, out _);
            Vector3 a = mainNode.TransformPoint(FistCentre(mainLeft));
            Vector3 b = offNode.TransformPoint(FistCentre(offLeft));
            Vector3 dir = b - a;
            if (dir.magnitude < 0.05f) return true;   // too close to aim: keep the current pose
            Vector3 v = grip2Pos - gripPos;
            Vector3 thumb = mainNode.rotation * HandFrame(mainLeft) * Vector3.up;
            Quaternion want = Quaternion.LookRotation(dir.normalized, thumb);
            Quaternion have = Quaternion.LookRotation(v.normalized, gripRot * Vector3.up);
            Quaternion rot = want * Quaternion.Inverse(have);
            prop.rotation = rot;
            prop.position = a - rot * gripPos;
            return true;
        }

        // ── Hand nodes ──────────────────────────────────────────────────

        public enum HandNodeKind
        {
            None,
            /// <summary>A HandTracker (local rig): side from handPreference / the driving anchor.</summary>
            Tracker,
            /// <summary>A node named LeftHand (RemoteRig).</summary>
            Left,
            /// <summary>A node named RightHand (RemoteRig).</summary>
            Right,
            /// <summary>A node named Hand (RemoteRig): whichever hand that player is using.</summary>
            Active,
        }

        public static string Normalize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '_' || c == '-') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>Kind of hand node <paramref name="t"/> is by itself (tracker, or RemoteRig-style name).</summary>
        public static HandNodeKind KindOf(Transform t, out HandTracker tracker)
        {
            tracker = null;
            if (t == null) return HandNodeKind.None;
            if (t.TryGetComponent<HandTracker>(out var ht)) { tracker = ht; return HandNodeKind.Tracker; }
            switch (Normalize(t.name))
            {
                case "lefthand": return HandNodeKind.Left;
                case "righthand": return HandNodeKind.Right;
                case "hand": return HandNodeKind.Active;
            }
            return HandNodeKind.None;
        }

        /// <summary>The nearest hand node strictly above <paramref name="from"/>, or null.</summary>
        public static Transform FindHandNode(Transform from, out HandNodeKind kind, out HandTracker tracker)
        {
            for (Transform p = from != null ? from.parent : null; p != null; p = p.parent)
            {
                kind = KindOf(p, out tracker);
                if (kind != HandNodeKind.None) return p;
            }
            kind = HandNodeKind.None;
            tracker = null;
            return null;
        }

        /// <summary>Which side a tracker's frame belongs to right now (Both: the anchor driving it).</summary>
        public static bool TrackerIsLeft(HandTracker tracker)
        {
            if (tracker == null) return false;
            switch (tracker.handPreference)
            {
                case HandTracker.HandPreference.Left: return true;
                case HandTracker.HandPreference.Right: return false;
                default: return tracker.DrivenByLeftAnchor;
            }
        }

        // ── Validation (HandheldKit.ValidateGO / ValidateRigGO) ─────────

        public enum IssueLevel { Info, Warn, Fail }

        public struct Issue
        {
            public IssueLevel level;
            /// <summary>Path of the prop below the validated root ("Player/BothHands/Blaster").</summary>
            public string path;
            public string message;
            public override string ToString() => $"{level.ToString().ToUpperInvariant()} {path}: {message}";
        }

        static string F(Vector3 v) => v.ToString("F3");
        static bool HasNegativeScale(Transform t) => t.localScale.x < 0 || t.localScale.y < 0 || t.localScale.z < 0;

        public static string PathOf(Transform t, Transform root)
        {
            if (t == null) return "";
            var s = t.name;
            for (var p = t.parent; p != null && root != null && p != root.parent; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        static bool IsMarker(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
                if (t.name == MuzzleName || GripNames.Contains(t.name)) return true;
            return false;
        }

        static List<Vector3> Points(Transform root)
        {
            var pts = new List<Vector3>();
            var w2l = root.worldToLocalMatrix;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<Renderer>() == null || (Application.isPlaying && !mf.sharedMesh.isReadable)) continue;
                if (IsMarker(mf.transform, root)) continue;
                var M = w2l * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) pts.Add(M.MultiplyPoint3x4(v));
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null || (Application.isPlaying && !smr.sharedMesh.isReadable)) continue;
                var M = w2l * smr.transform.localToWorldMatrix;
                foreach (var v in smr.sharedMesh.vertices) pts.Add(M.MultiplyPoint3x4(v));
            }
            return pts;
        }

        /// <summary>
        /// One hand-held prop on its own (HandheldKit.ValidateGO). Fail = it will sit or aim
        /// wrong; Warn = check it; Info = what the markers mean in the hand.
        /// <paramref name="needMuzzle"/> false for props that do not shoot / emit.
        /// </summary>
        public static List<Issue> ValidateProp(GameObject root, bool needMuzzle = true)
        {
            var issues = new List<Issue>();
            if (root == null) return issues;
            var rt = root.transform;
            string path = rt.name;
            void Add(IssueLevel l, string m) => issues.Add(new Issue { level = l, path = path, message = m });

            var grip = FindGrip(rt, false, out bool palm);
            var gripL = FindGrip(rt, true, out _);
            var muz = FindMuzzle(rt);
            if (grip == null)
                Add(IssueLevel.Fail, "missing Grip (or PalmGrip) child: the point that sits in the fist (or on the palm)");
            else if (grip.parent != rt)
                Add(IssueLevel.Warn, $"{grip.name} is nested ({PathOf(grip, rt)}): dp.hold finds it, but the convention (and builder recipes using localPosition) expect a DIRECT child of the prop root");
            if (muz == null && needMuzzle)
                Add(IssueLevel.Fail, "missing Muzzle child (barrel tip; +Z = shot direction). Props that do not shoot can skip it");
            else if (muz != null && muz.parent != rt)
                Add(IssueLevel.Warn, $"Muzzle is nested ({PathOf(muz, rt)}): dp.muzzle finds it, but the convention is a DIRECT child of the prop root");
            foreach (var t in rt.GetComponentsInChildren<Transform>(true))
                if (HasNegativeScale(t)) Add(IssueLevel.Fail, $"negative scale on {PathOf(t, rt)} {F(t.localScale)} — use a rotation, never a -1 scale");
            foreach (var n in GripNames.Concat(new[] { MuzzleName }))
            {
                var t = FindMarker(rt, n);
                if (t != null && (t.localScale - Vector3.one).sqrMagnitude > 1e-6f) Add(IssueLevel.Fail, $"{n} has scale {F(t.localScale)}: markers must be unscaled");
            }
            var ls = rt.localScale;
            if (Mathf.Abs(ls.x - ls.y) > 1e-3f || Mathf.Abs(ls.x - ls.z) > 1e-3f) Add(IssueLevel.Warn, $"non-uniform root scale {F(ls)}: put scale on Model");

            var pts = Points(rt);
            if (pts.Count == 0) return issues;   // no readable mesh: shape checks skipped
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts) b.Encapsulate(p);
            float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
            if (maxDim > 1.5f) Add(IssueLevel.Fail, $"prop is {maxDim:F2} m long: import scale is wrong (cm export?)");
            else if (maxDim < 0.02f) Add(IssueLevel.Warn, $"prop is only {maxDim * 100:F1} cm: scale?");
            foreach (var g in new[] { grip, gripL, FindMarker(rt, Grip2Name) }.Where(t => t != null).Distinct())
            {
                MarkerPose(rt, g, out var glp, out _);
                var bb = b; bb.Expand(0.04f);
                if (!bb.Contains(glp)) Add(IssueLevel.Fail, $"{g.name} {F(glp)} is outside the mesh (bounds {F(b.min)}..{F(b.max)})");
                if (g.name.StartsWith("Palm"))
                {
                    float near = Mathf.Sqrt(pts.Min(q => (q - glp).sqrMagnitude));
                    if (near > 0.01f) Add(IssueLevel.Warn, $"{g.name} is {near * 100:F1} cm from the surface: a PalmGrip is the contact point");
                }
            }
            if (palm && gripL == grip) Add(IssueLevel.Warn, "PalmGrip without PalmGripLeft: in the LEFT hand the side that touched the right palm faces away — add PalmGripLeft if that side matters");
            if (muz != null)
            {
                Vector3 d = rt.InverseTransformDirection(muz.forward), mp = rt.InverseTransformPoint(muz.position);
                float pm = Vector3.Dot(mp, d), lo = pts.Min(p => Vector3.Dot(p, d)), hi = pts.Max(p => Vector3.Dot(p, d)), ext = hi - lo;
                if (pm < lo + 0.6f * ext) Add(IssueLevel.Fail, $"Muzzle is not at the far end of the mesh along its own +Z {F(d)}: +Z points back into the prop, or the Muzzle is misplaced");
                if (hi - pm > Mathf.Max(0.03f, 0.1f * ext)) Add(IssueLevel.Fail, $"mesh extends {(hi - pm) * 100:F0} cm past the Muzzle along its +Z: the Muzzle is not at the tip, or +Z is not the barrel");
                Vector3 u = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized, v = Vector3.Cross(d, u);
                var front = pts.Where(q => Vector3.Dot(q, d) > hi - Mathf.Max(0.03f, 0.1f * ext)).ToList();
                if (front.Count > 0)
                {
                    float u0 = front.Min(q => Vector3.Dot(q, u)), u1 = front.Max(q => Vector3.Dot(q, u)), v0 = front.Min(q => Vector3.Dot(q, v)), v1 = front.Max(q => Vector3.Dot(q, v));
                    float mu = Vector3.Dot(mp, u), mv = Vector3.Dot(mp, v);
                    if (mu < u0 - 0.01f || mu > u1 + 0.01f || mv < v0 - 0.01f || mv > v1 + 0.01f) Add(IssueLevel.Fail, "Muzzle is beside the front end of the mesh, not on the barrel/tip");
                }
                if (grip != null)
                {
                    MarkerPose(rt, grip, out var gpos, out var grot);
                    Vector3 gz = grot * Vector3.forward, gy = grot * Vector3.up, rel = mp - gpos;
                    if (Vector3.Dot(rel, d) < 0) Add(IssueLevel.Fail, "the Grip is in front of the Muzzle along the shot direction");
                    if (Vector3.Dot(d, gz) > 0.9f && Vector3.Dot(rel, gy) < -0.02f) Add(IssueLevel.Fail, $"the barrel is {(-Vector3.Dot(rel, gy)) * 100:F0} cm BELOW the Grip on the thumb axis: the prop is upside down (pistol rule: barrel on the thumb side)");
                    if (Vector3.Dot(d, gy) > 0.9f && Vector3.Dot(rel, gy) < 0) Add(IssueLevel.Warn, "the tip is on the PINKY side of the fist: a reverse (ice-pick) grip — fine only if deliberate");
                    if (Vector3.Dot(d, gz) < 0.9f && Vector3.Dot(d, gy) < 0.9f) Add(IssueLevel.Warn, $"shot direction {F(d)} is neither along the fingers (Grip +Z) nor out of the thumb side (Grip +Y)");
                }
            }
            return issues;
        }

        sealed class Mounted { public string key, path; public Vector3 gripPos; public Quaternion gripRot; }

        /// <summary>
        /// Every hand-held mounted in a rig (HandheldKit.ValidateRigGO): each prop (Grip,
        /// PalmGrip, GripLeft, PalmGripLeft or Muzzle child) under a HandTracker or a RemoteRig
        /// LeftHand/RightHand/Hand. <paramref name="remounts"/> tells whether a script on the prop
        /// mounts it at runtime (dp.hold / handheld_mount.lua) — such props skip the authored-pose
        /// rules (the runtime mount is correct by construction) but are still checked for scale
        /// and markers.
        /// </summary>
        public static List<Issue> ValidateRig(GameObject root, System.Func<Transform, bool> remounts = null)
        {
            var issues = new List<Issue>();
            if (root == null) return issues;
            void Add(IssueLevel l, string path, string m) => issues.Add(new Issue { level = l, path = path, message = m });
            var local = new List<Mounted>();
            var remote = new List<Mounted>();
            var props = new List<Transform>();
            void Collect(Transform t)
            {
                if (t != root.transform && IsProp(t)) { props.Add(t); return; }
                foreach (Transform c in t) Collect(c);
            }
            Collect(root.transform);

            foreach (var prop in props)
            {
                string path = PathOf(prop, root.transform);
                Transform node = null; HandTracker tr = null; bool isRemote = false;
                for (var p = prop.parent; p != null; p = p.parent)
                {
                    tr = p.GetComponent<HandTracker>();
                    if (tr != null) { node = p; break; }
                    string n = Normalize(p.name);
                    if ((n == "lefthand" || n == "righthand" || n == "hand") && p.GetComponentsInParent<Transform>(true).Any(a => Normalize(a.name) == "remoterig"))
                    { node = p; isRemote = true; break; }
                }
                if (node == null) continue;   // not in a hand (a pickup lying in the world): nothing to check here

                string side;
                if (tr != null) side = tr.handPreference == HandTracker.HandPreference.Left ? "left" : tr.handPreference == HandTracker.HandPreference.Right ? "right" : "active";
                else { string n = Normalize(node.name); side = n == "lefthand" ? "left" : n == "righthand" ? "right" : "active"; }
                bool flip = tr != null && tr.flipVisual, left = side == "left";
                bool runtime = remounts != null && remounts(prop);
                var grip = FindGrip(prop, left, out bool palm);
                var muz = FindMuzzle(prop);
                if (grip == null) Add(IssueLevel.Fail, path, "missing Grip (author it on the prop: the fist point; +Z where the fingers point, +Y thumb side)");
                for (var p = prop; p != null && p != node.parent; p = p.parent)
                    if (HasNegativeScale(p)) Add(IssueLevel.Fail, path, $"negative scale on {PathOf(p, root.transform)} {F(p.localScale)} — use a rotation per side, never a -1 scale");
                foreach (var t in prop.GetComponentsInChildren<Transform>(true))
                    if (t != prop && HasNegativeScale(t)) Add(IssueLevel.Fail, path, $"negative scale inside the prop on {t.name}");

                string frame = isRemote ? "RemoteRig raw anchor" : (left && flip ? "HandTracker+flipVisual (left)" : "HandTracker raw anchor");
                string pivotHint = "";
                for (var p = prop.parent; p != null && p != node; p = p.parent)
                    if (p.name.Contains("Handheld Pivot"))
                        pivotHint = $" [hangs on '{p.name}' (euler {F(p.localEulerAngles)}): the old SDK template pivot predates the OpenXR wrist frame — mount on the hand node with dp.hold or HandheldMath.Place]";

                if (grip != null)
                {
                    Vector3 gp = node.InverseTransformPoint(grip.position);
                    Quaternion gr = Quaternion.Inverse(node.rotation) * grip.rotation;
                    ExpectedGripPose(left, flip, tr ? tr.flipVisualRotation : Vector3.zero, tr ? tr.flipVisualPosition : Vector3.zero, out var ep, out var er, palm);
                    float ang = Quaternion.Angle(gr, er), dist = (gp - ep).magnitude;
                    if (!runtime)
                    {
                        if (ang > AngleTolerance) Add(IssueLevel.Fail, path, $"({side}, {frame}): {grip.name} is rotated {ang:F0}° from the device grip; is euler {F(gr.eulerAngles)} in the hand frame, must be {F(er.eulerAngles)} (fingers = Grip +Z, thumb side = Grip +Y). Mount it with dp.hold(prop, '{(side == "active" ? "active" : side)}'){pivotHint}");
                        if (dist > GripTolerance) Add(IssueLevel.Fail, path, $"({side}): {grip.name} at {F(gp)} is {dist * 100:F1} cm from the {(palm ? "palm surface" : "fist centre")} {F(ep)}{pivotHint}");
                    }
                    (isRemote ? remote : local).Add(new Mounted { key = prop.name + "|" + side, path = path, gripPos = gp, gripRot = gr });
                }
                else if (muz != null)
                {
                    var mf = Quaternion.Inverse(node.rotation) * muz.forward;
                    if (Vector3.Dot(mf, Vector3.forward) < 0.9f) Add(IssueLevel.Warn, path, $"Muzzle +Z = {F(mf)} in the hand frame, not along the fingers");
                }
                if (side == "active" && !runtime)
                    Add(IssueLevel.Warn, path, $"on an ACTIVE-HAND node ({node.name}) — authored for the RIGHT hand; the LEFT hand is {(flip ? "oriented correctly but sits on the wrong side of the palm (fist y mirrored)" : "upside down")} unless it re-mounts at runtime: call dp.hold(prop, 'active') from its script");
            }
            foreach (var r in remote)
            {
                var l = local.FirstOrDefault(x => x.key == r.key);
                if (l == null) continue;
                float d = (r.gripPos - l.gripPos).magnitude, a = Quaternion.Angle(r.gripRot, l.gripRot);
                if (d > RemoteTolerance || a > RemoteAngleTolerance)
                    Add(IssueLevel.Fail, r.path, $"remote copy differs from local {l.path} by {d * 100:F1} cm / {a:F0}° (relative to the hand node): other players see it in the wrong place");
            }
            return issues;
        }
    }
}
