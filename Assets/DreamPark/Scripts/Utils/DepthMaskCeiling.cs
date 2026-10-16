namespace DreamPark
{
    using UnityEngine;

    /// <summary>
    /// Holds the overhead depth-mask plane LEVEL and a fixed distance above the eye.
    /// Authored onto a plane under HeadTracker, as a sibling of DepthMaskDistance.
    ///
    /// WHY THERE IS A SECOND MASK AT ALL. DepthMaskDistance is 40 x 15 m at z = 7, and
    /// at MaskBias 0.4 it writes its depth at 0.6 * 7 = 4.2 m — so it exempts real
    /// geometry FARTHER than 4.2 m and nothing nearer. A real ceiling sits under a
    /// metre and a half above your eyes, well inside that, so it keeps occluding and
    /// keeps flickering. This plane sits at 0.8 m, writes at ~0.5 m straight up, and
    /// clears exactly that near-overhead band. Being CLOSE is the whole point of it;
    /// it is not covering angles the front mask misses (that one pitches with the head
    /// and covers +/-47 degrees of wherever you look), it is covering distances.
    ///
    /// WHICH BOUNDS heightAboveHead FROM ABOVE. The exemption only reaches past a real
    /// surface while 0.4 * height stays below it, so a plane raised toward the real
    /// ceiling stops clearing it. Lower is stronger, and "just above everything" is the
    /// wrong instinct — content height is irrelevant, since the mask edits the
    /// real-world depth map and never clips virtual geometry.
    ///
    /// WHY IT CANNOT JUST BE PARENTED AND LEFT ALONE. HeadTracker copies the head's
    /// full rotation, and DepthMaskDistance WANTS that — staying square to your view is
    /// what keeps it in front of you. This one wants the inverse: the head's position,
    /// and of its orientation only the yaw. An untouched child pitches with the head and
    /// "up" swings behind you the moment you look up. That single inversion is the whole
    /// reason this file exists rather than the plane being pure authoring.
    ///
    /// OCT 11 2026 — THE DOME (playtest: "overhead objects occluded"). The flat plane was
    /// replaced by a mesh BUILT IN CODE (BuildDomeMesh): a level plane heightAboveHead
    /// above the eye UNION a half-sphere of domeRadius around the eye. Seen from the eye
    /// the mask surface lies at DomeDistance = max(R, h / sin(elevation)) in every upward
    /// direction: a sphere cap of radius R overhead, flattening into the plane where the
    /// plane is farther (elevation below asin(h/R)). Faces point at the eye. It is ONE
    /// surface on purpose: Meta renders mask meshes closest-wins, so a separate plane and
    /// sphere would give min(), not max(). Meta's rule (DepthMask.shader pass 1) exempts
    /// real depth farther than maskDistance * (1 - MaskBias); with bias 0.02 that is
    /// "everything overhead beyond ~0.83 m" — a ceiling, lamp or doorframe never occludes,
    /// while your own raised hand (within reach) still does. The old plane cleared
    /// straight up only beyond 0.48 m, and the global bias was fought over by three writers.
    /// (Everything above about the head-height paragraphs refers to the old plane.)
    ///
    /// SOLE MaskBias OWNER. EnvironmentDepthManager.MaskBias is one global for every mask
    /// mesh. This component writes it every LateUpdate (maskBias, 0.02); DepthMask and
    /// ProceduralLavaPit no longer write it. DepthMaskDistance moved 7 → 4.3 m so it still
    /// exempts beyond 4.3 * 0.98 ≈ 4.2 m, what 7 * (1 - 0.4) gave before.
    ///
    /// OCT 13 2026 — THE SKY. Outdoors, overhead content vanished near the sun and against
    /// open sky. DepthMaskAudit showed why: where the depth cameras measure nothing they report
    /// depth 0 — the near plane, 0.10 m — and Meta's occlusion reads that as a surface 10 cm
    /// from your eyes (22 of 36 overhead occlusions in the outdoor run). No mask can exempt it:
    /// a mask only exempts real depth FARTHER than itself. So this component also swaps Meta's
    /// mask material onto DreamPark/EnvironmentDepth/DepthMaskSanitize (Resources/Shaders),
    /// Meta's shader with one extra rule in its combine pass: real depth nearer than
    /// invalidDepthMeters becomes "no data" (1.0) before any occlusion reads it. Pure Meta
    /// depth — hands are never measured that close, so they keep occluding. The combine pass
    /// runs every frame because the dome is always registered.
    ///
    /// OCT 13 2026 — SKY FILTER. With invalid depth gone, the remaining sky flicker was phantom
    /// surfaces at 0.2-0.6 m that come and go frame to frame — the same distances as a raised
    /// hand. Per depth texel whose ray points above the horizon (ramped in skyStartDeg ->
    /// skyFullDeg), real depth now only occludes if it persisted over the last
    /// skyHistoryFrames depth frames (see DepthMaskSanitize pass 1/2). A hand persists; the
    /// phantoms don't. Below the horizon nothing changes. The history is fed from Meta's raw
    /// depth event, three rotating texture arrays, so the combine at frame t reads t-1, t-2.
    ///
    /// HAND GATE (third outdoor run). Some sky phantoms hold still for seconds, beyond any time
    /// filter, at hand distances. Aidan: the ceiling and the sky never need to occlude. So above
    /// skyFullDeg real depth only occludes where its point lies inside a rough capsule around a
    /// tracked hand / forearm stub (handRadius, forearmLength, forearmRadius) — the occlusion is
    /// still Meta depth, the pose only says where it may count. Not an occluder object, no
    /// gestures; loose tracking is fine at these radii. No hand tracked = nothing occludes up there.
    ///
    /// SAFETY + GROUNDED (playtest 8, "the horizon is cutting off the gameplay"). Below the
    /// gate, open lawn and bay read as a phantom ~1.2 m away and cut everything near the
    /// horizon. Phantoms float (farther ground right below them in the depth image); people and
    /// objects are grounded. Floating surfaces beyond safetyNearKeep stop occluding; grounded,
    /// non-floor surfaces within safetyRadius punch through ALL virtual content — Aidan: things
    /// you could walk into or trip on must show through the game even if not depth-accurate.
    /// Floor (faces up, or within safetyFloorClearance of the floor) never punches.
    ///
    /// LIVE TUNING: {persistentDataPath}/depthmask-tune.txt, key=value per line (any public
    /// field below: skyFilter, skyStartDeg, skyFullDeg, skyHistoryFrames, handGate, handRadius,
    /// forearmLength, forearmRadius, safety, safetyRadius, safetyNearKeep,
    /// safetyFloorClearance, safetyFloorNormalY, safetyPunchMeters, dropInvalidDepth, invalidDepthMeters, maskBias, domeRadius,
    /// heightAboveHead). Re-read every 2 s;
    /// adb push it while the headset is on.
    ///
    /// DEVICE CHECK (debugMaskCheck, ForceMaskCheck, or the flag file
    /// {persistentDataPath}/depthmask-check): heightAboveHead 0.1 + bias 0.9. If overhead
    /// content is STILL occluded with that, the mask is not applied at all
    /// (provider/registration), not mis-tuned.
    /// </summary>
    [DisallowMultipleComponent]
    public class DepthMaskCeiling : MonoBehaviour
    {
        private static DepthMaskCeiling _instance;

        /// <summary>
        /// True while an enabled ceiling plane exists. A STATUS READOUT FOR DEBUG UI, and
        /// nothing more — it is not the gate, and nothing currently reads it.
        ///
        /// It once was the plan: let the templates check this, so unchecking the plane on
        /// the rig handed the ceilings back to them. That fails exactly where it matters.
        /// An SDK creator in the editor has no rig at all, so Active is false there and
        /// every attraction and prop would quietly build the legacy patchwork the plane
        /// exists to replace — the editor would stop resembling the headset precisely
        /// when someone was using it to judge occlusion. AllowTemplateCeilings below is
        /// the real switch, and it is off everywhere until a human turns it on.
        /// </summary>
        public static bool Active => _instance != null && _instance.isActiveAndEnabled;

        /// <summary>
        /// Master switch for the LEGACY per-attraction and per-prop ceilings. OFF.
        ///
        /// Deliberately a static rather than a lowered default on LevelTemplate's
        /// serialized generateCeiling flag. Unity only applies a field's default where no
        /// value was serialized, and every shipped attraction prefab already has
        /// generateCeiling serialized TRUE — so flipping that default would change
        /// nothing whatsoever for content already on disk, while looking like it had.
        /// A code-side gate is the only kind that is genuinely off for existing content.
        ///
        /// PropTemplate has no such flag and must not grow one. Props never shipped with
        /// ceiling fields, so adding any would write fresh lines into every prop prefab
        /// the next time ContentProcessor re-saves one — re-bundling the whole catalog to
        /// carry a value this gate already overrules. Both templates size their legacy
        /// ceilings from private CeilingPadding / CeilingHeight constants for the same
        /// reason: an author cannot reach the legacy path, so nothing about it belongs in
        /// serialized content.
        ///
        /// Set true (debug menu, test harness, one line in a bootstrap) to bring the old
        /// per-template ceilings back for a comparison run.
        /// </summary>
        public static bool AllowTemplateCeilings = false;

        [Tooltip("Metres above the player's EYE, not above the floor. LOWER IS STRONGER: " +
                 "it lowers the distance at which real geometry stops occluding. Must stay " +
                 "comfortably below the real ceiling, or the exemption no longer reaches it.")]
        public float heightAboveHead = 0.25f;

        [Tooltip("Radius (m) of the half-sphere around the EYE. Real depth nearer than " +
                 "radius * (1 - maskBias) still occludes overhead — that keeps your own raised " +
                 "hand in front of content. Farther than that never occludes.")]
        public float domeRadius = 0.85f;

        [Tooltip("Horizontal reach (m) of the level plane around the eye.")]
        public float planeRadius = 30f;

        [Tooltip("EnvironmentDepthManager.MaskBias, written EVERY LateUpdate. This component " +
                 "is its sole owner — it is global for every mask mesh in the park.")]
        [Range(0f, 0.95f)]
        public float maskBias = 0.02f;

        [Tooltip("Treat real depth nearer than invalidDepthMeters as 'no data' (sky, sun glare). " +
                 "Swaps Meta's mask material onto DreamPark/EnvironmentDepth/DepthMaskSanitize.")]
        public bool dropInvalidDepth = true;

        [Tooltip("Real depth at or nearer than this (m) is a failed measurement, not a surface. The " +
                 "sensor reports failures at its near plane, 0.10 m.")]
        public float invalidDepthMeters = 0.15f;

        public const string SanitizeShaderName = "DreamPark/EnvironmentDepth/DepthMaskSanitize";
        private static readonly int InvalidDepthMetersId = Shader.PropertyToID("_InvalidDepthMeters");
        private Material _maskMaterial;
        private Shader _metaMaskShader, _sanitizeShader;
        private bool _sanitizeFailedLogged;

        [Tooltip("Sky filter: in depth texels looking above the horizon, real depth only occludes if " +
                 "it persisted over the last skyHistoryFrames depth frames. A hand persists; sky " +
                 "phantoms flicker. Needs dropInvalidDepth (it lives in the same shader).")]
        public bool skyFilter = true;

        [Tooltip("Ray elevation (degrees above horizontal) where the sky filter starts ramping in.")]
        public float skyStartDeg = 5f;

        [Tooltip("Ray elevation (degrees). Above it the ceiling and sky never occlude: real depth only " +
                 "counts inside the tracked-hand capsules (handGate). Between skyStartDeg and this the " +
                 "time filter ramps in.")]
        public float skyFullDeg = 15f;

        [Tooltip("Depth frames of memory at full strength (~33 ms each at 30 Hz). A hand sweeping " +
                 "into sky content starts occluding this many frames late; nothing trails behind.")]
        [Range(0, 2)]
        public int skyHistoryFrames = 2;

        [Tooltip("Above skyFullDeg, keep real depth inside rough capsules around tracked hands so your " +
                 "hand still occludes sky content. Off = nothing occludes up there.")]
        public bool handGate = true;

        [Tooltip("Capsule radius (m) around wrist -> middle fingertip.")]
        public float handRadius = 0.10f;

        [Tooltip("Forearm stub length (m) back from the wrist, along the hand's axis.")]
        public float forearmLength = 0.25f;

        [Tooltip("Forearm stub radius (m).")]
        public float forearmRadius = 0.07f;

        [Tooltip("Below the gate: phantom (floating) surfaces stop occluding, and grounded obstacles " +
                 "within safetyRadius punch through all virtual content.")]
        public bool safety = true;

        [Tooltip("Horizontal radius (m) around you inside which grounded people/objects show through " +
                 "virtual content even when it is in front of them.")]
        public float safetyRadius = 2.0f;

        [Tooltip("Nearer than this (m), real depth is always kept as measured (no floating test).")]
        public float safetyNearKeep = 0.8f;

        [Tooltip("Height (m) above the floor that still counts as floor and never punches through.")]
        public float safetyFloorClearance = 0.08f;

        [Tooltip("Surfaces whose normal is at least this vertical count as floor (slopes, lawn).")]
        [Range(0f, 1f)]
        public float safetyFloorNormalY = 0.8f;

        [Tooltip("Depth (m) written for punch-through pixels: virtual content farther than this is hidden there.")]
        public float safetyPunchMeters = 0.2f;

        public const string TuneFileName = "depthmask-tune.txt";
        private static readonly int SafeOnId = Shader.PropertyToID("_SafeOn");
        private static readonly int SafeEyeId = Shader.PropertyToID("_SafeEye");
        private static readonly int SafeFloorYId = Shader.PropertyToID("_SafeFloorY");
        private static readonly int SafeRadiusId = Shader.PropertyToID("_SafeRadius");
        private static readonly int SafeNearKeepId = Shader.PropertyToID("_SafeNearKeep");
        private static readonly int SafeFloorClearanceId = Shader.PropertyToID("_SafeFloorClearance");
        private static readonly int SafeFloorNormalYId = Shader.PropertyToID("_SafeFloorNormalY");
        private static readonly int SafePunchId = Shader.PropertyToID("_SafePunchMeters");
        private OVRCameraRig _rig;
        private static readonly int SkyHandAId = Shader.PropertyToID("_SkyHandA");
        private static readonly int SkyHandBId = Shader.PropertyToID("_SkyHandB");
        private static readonly int SkyHandCountId = Shader.PropertyToID("_SkyHandCount");
        private readonly Vector4[] _handA = new Vector4[4];
        private readonly Vector4[] _handB = new Vector4[4];
        private OVRSkeleton[] _skeletons = System.Array.Empty<OVRSkeleton>();
        private float _nextSkeletonSearch;

        /// <summary>Hand capsules sent to the depth shader this frame (0 = none tracked / gate off).</summary>
        public int HandCapsulesInUse { get; private set; }
        private static readonly int ReprojectionId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
        private static readonly int SkyHistory1Id = Shader.PropertyToID("_SkyHistory1");
        private static readonly int SkyHistory2Id = Shader.PropertyToID("_SkyHistory2");
        private static readonly int SkyInvId = Shader.PropertyToID("_SkyInvReprojection");
        private static readonly int SkyOnId = Shader.PropertyToID("_SkyFilterOn");
        private static readonly int SkyStartId = Shader.PropertyToID("_SkyStartDeg");
        private static readonly int SkyFullId = Shader.PropertyToID("_SkyFullDeg");
        private static readonly int SkyCountId = Shader.PropertyToID("_SkyHistoryCount");
        private static readonly int SkySourceId = Shader.PropertyToID("_SkySource");
        private System.Reflection.FieldInfo _fDepthEvent;
        private System.Action<RenderTexture> _onRawDepth;
        private Meta.XR.EnvironmentDepth.EnvironmentDepthManager _hookedManager;
        private readonly RenderTexture[] _skyRing = new RenderTexture[3];
        private int _skyNewest;
        private int _skyValid;
        private Material _skyCopyMaterial;
        private readonly Matrix4x4[] _skyInv = new Matrix4x4[2];
        private string _tunePath;
        private System.DateTime _tuneStamp;

        /// <summary>History frames the last combine actually used (0 = sky filter idle).</summary>
        public int SkyFramesInUse { get; private set; }

        /// <summary>True while Meta's mask material is on the sanitizing shader.</summary>
        public bool SanitizeActive => _maskMaterial != null && _sanitizeShader != null && _maskMaterial.shader == _sanitizeShader;

        [Tooltip("Mesh resolution: segments around, rings on the sphere cap.")]
        public int azimuthSegments = 64;
        public int capRings = 12;

        [Tooltip("DEVICE CHECK: heightAboveHead 0.1 + maskBias 0.9. Still occluded overhead " +
                 "with this on means the mask is not applied at all. Also ForceMaskCheck, or " +
                 "the flag file {persistentDataPath}/depthmask-check.")]
        public bool debugMaskCheck;

        /// <summary>Code / debug-menu switch for the device check (same as debugMaskCheck).</summary>
        public static bool ForceMaskCheck;

        public const float CheckHeightAboveHead = 0.1f;
        public const float CheckMaskBias = 0.9f;
        public const string CheckFlagFileName = "depthmask-check";

        [Tooltip("LEGACY, ignored since the Oct 11 2026 dome (the mesh is built in code, already " +
                 "facing the eye); kept so serialized rigs load unchanged. Was: " +
                 "Orientation forced every frame. It has to point the mesh's normal DOWN at " +
                 "the player: mask meshes are rendered with back-face culling, so a plane " +
                 "facing up masks nothing at all. (180, 0, 0) is correct for Unity's built-in " +
                 "Plane, whose normal is +Y. A Quad, whose normal is -Z, wants (-90, 0, 0).")]
        public Vector3 levelEulerAngles = new Vector3(180f, 0f, 0f);

        [Tooltip("Turn with the head about the VERTICAL axis while staying flat. Only " +
                 "observable on a plane that is not square — a square one covers the same " +
                 "ground however it is spun. On a duplicate of DepthMaskDistance, which is " +
                 "40 x 15, this keeps the 40 m axis across your view.")]
        public bool matchHeadYaw = true;

        [Tooltip("Transform to follow. Empty uses Camera.main, which is the authoritative " +
                 "head pose — deliberately not the HeadTracker parent, which also writes " +
                 "itself in LateUpdate and would leave this a frame behind.")]
        public Transform head;

        private static Camera _mainCamera;

        private MeshFilter _meshFilter;
        private Mesh _domeMesh;
        private float _builtHeight = float.NaN, _builtRadius = float.NaN, _builtOuter = float.NaN;
        private int _builtSegments, _builtRings;

        private Meta.XR.EnvironmentDepth.EnvironmentDepthManager _depthManager;
        private float _nextManagerSearch;
        private bool _loggedRegistration;
        private bool _lastCheck;
        private float _nextFlagPoll;
        private bool _flagFilePresent;
        private string _flagPath;

        /// <summary>True while the device check (h 0.1, bias 0.9) is in force.</summary>
        public bool CheckActive => debugMaskCheck || ForceMaskCheck || _flagFilePresent;

        /// <summary>heightAboveHead actually used this frame (the check overrides it).</summary>
        public float EffectiveHeight => CheckActive ? CheckHeightAboveHead : heightAboveHead;

        /// <summary>MaskBias actually written this frame (the check overrides it).</summary>
        public float EffectiveBias => CheckActive ? CheckMaskBias : maskBias;

        private void OnEnable()
        {
            _instance = this;
            try
            {
                _flagPath = System.IO.Path.Combine(Application.persistentDataPath, CheckFlagFileName);
                _tunePath = System.IO.Path.Combine(Application.persistentDataPath, TuneFileName);
            }
            catch { _flagPath = _tunePath = null; }
            PollFlagFile(force: true);
            EnsureDomeMesh();
            Snap();
            Debug.Log($"[DepthMaskCeiling] Active — dome mask: plane {EffectiveHeight}m above the eye ∪ " +
                      $"sphere R={domeRadius}m, MaskBias {EffectiveBias} (sole owner)" +
                      (CheckActive ? " [DEVICE CHECK ON]" : "") + ". Per-template ceilings are off.");
        }

        private void OnDisable()
        {
            if (_instance == this) _instance = null;
            // A dome left frozen at its last pose would exempt the wrong region.
            if (_depthManager != null && _meshFilter != null && _depthManager.MaskMeshFilters != null)
                _depthManager.MaskMeshFilters.Remove(_meshFilter);
            _loggedRegistration = false;
            UnhookRawDepth();
            _skyValid = 0;
        }

        private void OnDestroy()
        {
            if (_domeMesh != null) Destroy(_domeMesh);
            UnhookRawDepth();
            for (int i = 0; i < _skyRing.Length; i++)
                if (_skyRing[i] != null) { _skyRing[i].Release(); Destroy(_skyRing[i]); _skyRing[i] = null; }
            if (_skyCopyMaterial != null) Destroy(_skyCopyMaterial);
        }

        private void LateUpdate()
        {
            PollFlagFile(force: false);
            bool check = CheckActive;
            if (check != _lastCheck)
            {
                _lastCheck = check;
                Debug.Log($"[DepthMaskCeiling] device check {(check ? "ON" : "off")} — h={EffectiveHeight} bias={EffectiveBias}");
            }
            EnsureDomeMesh();
            Snap();
            OwnMaskBias();
        }

        /// <summary>
        /// The sole MaskBias writer, every frame — so nothing that writes it once (an old
        /// content build, a stray DepthMask) can keep it. Also re-registers the dome if the
        /// manager came up after DepthMask gave up (DepthMask disables itself when it finds
        /// no manager on enable).
        /// </summary>
        private void OwnMaskBias()
        {
            if (_depthManager == null)
            {
                if (Time.unscaledTime < _nextManagerSearch) return;
                _nextManagerSearch = Time.unscaledTime + 1f;
                _depthManager = FindFirstObjectByType<Meta.XR.EnvironmentDepth.EnvironmentDepthManager>();
                if (_depthManager == null) return;
            }

            _depthManager.MaskBias = EffectiveBias;
            SanitizeInvalidDepth();
            if (_hookedManager != _depthManager) HookRawDepth();
            UpdateHandCapsules();
            UpdateSafety();

            if (_meshFilter != null && _meshFilter.sharedMesh != null)
            {
                var list = _depthManager.MaskMeshFilters;
                if (list == null)
                {
                    list = new System.Collections.Generic.List<MeshFilter>();
                    _depthManager.MaskMeshFilters = list;
                }
                if (!list.Contains(_meshFilter)) list.Add(_meshFilter);
            }

            if (!_loggedRegistration)
            {
                _loggedRegistration = true;
                int n = _depthManager.MaskMeshFilters != null ? _depthManager.MaskMeshFilters.Count : 0;
                Debug.Log($"[DepthMaskCeiling] registered with EnvironmentDepthManager — {n} mask mesh(es), " +
                          $"MaskBias {EffectiveBias}, occlusion {_depthManager.OcclusionShadersMode}, " +
                          $"removeHands {_depthManager.RemoveHands}");
            }
        }

        /// <summary>
        /// Keep Meta's mask material on the sanitizing shader (or back on Meta's own when
        /// dropInvalidDepth is off). Meta creates the material lazily on the first depth frame
        /// with a mask registered, and keeps it private — hence the reflection, which is logged
        /// once and skipped if a future SDK renames it.
        /// </summary>
        private void SanitizeInvalidDepth()
        {
            if (_maskMaterial == null)
            {
                var fMask = typeof(Meta.XR.EnvironmentDepth.EnvironmentDepthManager).GetField("_mask",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                object mask = fMask != null ? fMask.GetValue(_depthManager) : null;
                if (mask == null)
                {
                    if (fMask == null) LogSanitizeFailure("EnvironmentDepthManager._mask not found");
                    return; // not created yet: no depth frame with a mask so far
                }
                var fMat = mask.GetType().GetField("_maskMaterial",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                _maskMaterial = fMat != null ? fMat.GetValue(mask) as Material : null;
                if (_maskMaterial == null) { LogSanitizeFailure("Mask._maskMaterial not found"); return; }
                _metaMaskShader = _maskMaterial.shader;
                _sanitizeShader = Shader.Find(SanitizeShaderName);
                if (_sanitizeShader == null) { LogSanitizeFailure(SanitizeShaderName + " not in the build"); return; }
            }

            Shader want = dropInvalidDepth && _sanitizeShader != null ? _sanitizeShader : _metaMaskShader;
            if (want != null && _maskMaterial.shader != want)
            {
                _maskMaterial.shader = want;
                Debug.Log($"[DepthMaskCeiling] mask material -> {want.name}" +
                          (want == _sanitizeShader ? $" (real depth <= {invalidDepthMeters} m is 'no data')" : ""));
            }
            _maskMaterial.SetFloat(InvalidDepthMetersId, invalidDepthMeters);
        }

        // ------------------------------------------------------------------ sky filter

        private void HookRawDepth()
        {
            UnhookRawDepth();
            if (_depthManager == null) return;
            _fDepthEvent ??= typeof(Meta.XR.EnvironmentDepth.EnvironmentDepthManager).GetField("onDepthTextureUpdate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (_fDepthEvent == null) { LogSanitizeFailure("EnvironmentDepthManager.onDepthTextureUpdate not found (sky filter only)"); return; }
            _onRawDepth ??= OnRawDepth;
            var existing = _fDepthEvent.GetValue(_depthManager) as System.Action<RenderTexture>;
            _fDepthEvent.SetValue(_depthManager, (System.Action<RenderTexture>)System.Delegate.Combine(existing, _onRawDepth));
            _hookedManager = _depthManager;
        }

        private void UnhookRawDepth()
        {
            if (_hookedManager == null || _fDepthEvent == null || _onRawDepth == null) { _hookedManager = null; return; }
            var existing = _fDepthEvent.GetValue(_hookedManager) as System.Action<RenderTexture>;
            _fDepthEvent.SetValue(_hookedManager, (System.Action<RenderTexture>)System.Delegate.Remove(existing, _onRawDepth));
            _hookedManager = null;
        }

        /// <summary>
        /// Meta raises this with each NEW raw depth frame, inside its onBeforeRender and just
        /// before it runs the mask combine. So: point the combine at the previous frames'
        /// history, then write this frame into the oldest slot for the next combine.
        /// </summary>
        private void OnRawDepth(RenderTexture raw)
        {
            if (raw == null || !isActiveAndEnabled || _maskMaterial == null || _sanitizeShader == null ||
                _maskMaterial.shader != _sanitizeShader)
                return;

            bool on = skyFilter && raw.dimension == UnityEngine.Rendering.TextureDimension.Tex2DArray;
            Matrix4x4[] reproj = null;
            if (on)
            {
                // World -> depth clip as of the previous frame (Meta publishes it after the
                // combine); one depth frame stale, well inside the 5-20 degree ramp.
                try { reproj = Shader.GetGlobalMatrixArray(ReprojectionId); } catch { reproj = null; }
                if (reproj == null || reproj.Length < 2 || reproj[0] == Matrix4x4.zero) on = false;
            }

            if (on)
            {
                _skyInv[0] = reproj[0].inverse;
                _skyInv[1] = reproj[1].inverse;
                _maskMaterial.SetMatrixArray(SkyInvId, _skyInv);
                if (_skyValid > 0)
                {
                    _maskMaterial.SetTexture(SkyHistory1Id, _skyRing[_skyNewest]);
                    _maskMaterial.SetTexture(SkyHistory2Id, _skyRing[_skyValid > 1 ? (_skyNewest + 2) % 3 : _skyNewest]);
                }
            }
            SkyFramesInUse = on ? Mathf.Min(skyHistoryFrames, _skyValid) : 0;
            _maskMaterial.SetFloat(SkyOnId, on ? 1f : 0f);
            _maskMaterial.SetFloat(SkyCountId, SkyFramesInUse);
            _maskMaterial.SetFloat(SkyStartId, skyStartDeg);
            _maskMaterial.SetFloat(SkyFullId, skyFullDeg);
            if (!on || skyHistoryFrames <= 0) { _skyValid = 0; return; }

            // Write this frame into the oldest slot — never one the combine is about to read.
            int slot = (_skyNewest + 1) % 3;
            if (!EnsureSkyRing(raw)) return;
            if (_skyCopyMaterial == null) _skyCopyMaterial = new Material(_sanitizeShader);
            _skyCopyMaterial.SetTexture(SkySourceId, raw);
            _skyCopyMaterial.SetFloat(InvalidDepthMetersId, invalidDepthMeters);
            var prevActive = RenderTexture.active;
            Graphics.SetRenderTarget(_skyRing[slot], 0, CubemapFace.Unknown, -1);
            _skyCopyMaterial.SetPass(2);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 3, 2);
            RenderTexture.active = prevActive;
            _skyNewest = slot;
            _skyValid = Mathf.Min(_skyValid + 1, 2);
        }

        /// <summary>
        /// Rough capsules around each tracked hand (wrist -> middle fingertip) and a forearm stub
        /// back from the wrist, in world space, for the depth shader's hand gate. Pose data only:
        /// nothing is rendered, and nothing here occludes by itself.
        /// </summary>
        private void UpdateHandCapsules()
        {
            int n = 0;
            if (handGate && SanitizeActive)
            {
                if (Time.unscaledTime >= _nextSkeletonSearch)
                {
                    _nextSkeletonSearch = Time.unscaledTime + 1f;
                    _skeletons = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
                }
                foreach (var sk in _skeletons)
                {
                    if (n > 2 || sk == null || !sk.IsInitialized || !sk.IsDataValid || sk.Bones == null) continue;
                    var type = sk.GetSkeletonType();
                    bool xr = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;
                    bool hand = xr || type == OVRSkeleton.SkeletonType.HandLeft || type == OVRSkeleton.SkeletonType.HandRight;
                    if (!hand) continue;
                    // BoneId values overlap between skeleton types: compare only within the right set.
                    var wristId = xr ? OVRSkeleton.BoneId.XRHand_Wrist : OVRSkeleton.BoneId.Hand_WristRoot;
                    var tipId = xr ? OVRSkeleton.BoneId.XRHand_MiddleTip : OVRSkeleton.BoneId.Hand_MiddleTip;
                    var baseId = xr ? OVRSkeleton.BoneId.XRHand_MiddleMetacarpal : OVRSkeleton.BoneId.Hand_Middle1;
                    Transform wrist = null, tip = null, knuckle = null;
                    foreach (var b in sk.Bones)
                    {
                        if (b == null || b.Transform == null) continue;
                        if (b.Id == wristId) wrist = b.Transform;
                        else if (b.Id == tipId) tip = b.Transform;
                        else if (b.Id == baseId) knuckle = b.Transform;
                    }
                    if (wrist == null || tip == null) continue;
                    Vector3 w = wrist.position, t = tip.position;
                    _handA[n] = new Vector4(w.x, w.y, w.z, handRadius);
                    _handB[n] = t;
                    n++;
                    Vector3 back = w - (knuckle != null && (knuckle.position - w).sqrMagnitude > 1e-4f ? knuckle.position : t);
                    if (forearmLength > 0f && back.sqrMagnitude > 1e-6f)
                    {
                        Vector3 e = w + back.normalized * forearmLength;
                        _handA[n] = new Vector4(w.x, w.y, w.z, forearmRadius);
                        _handB[n] = e;
                        n++;
                    }
                }
            }
            HandCapsulesInUse = n;
            if (_maskMaterial == null) return;
            _maskMaterial.SetVectorArray(SkyHandAId, _handA);
            _maskMaterial.SetVectorArray(SkyHandBId, _handB);
            _maskMaterial.SetFloat(SkyHandCountId, n);
        }

        /// <summary>Eye, floor and thresholds for the depth shader's safety / grounded test.</summary>
        private void UpdateSafety()
        {
            if (_maskMaterial == null) return;
            Transform eye = ResolveHead();
            bool on = safety && SanitizeActive && eye != null;
            _maskMaterial.SetFloat(SafeOnId, on ? 1f : 0f);
            if (!on) return;

            // Floor-level tracking origin: the tracking space's origin IS the floor. Without a
            // rig, assume a standing eye height.
            if (_rig == null) _rig = FindFirstObjectByType<OVRCameraRig>();
            float floorY = _rig != null && _rig.trackingSpace != null &&
                           OVRManager.instance != null &&
                           OVRManager.instance.trackingOriginType != OVRManager.TrackingOrigin.EyeLevel
                ? _rig.trackingSpace.position.y
                : eye.position.y - 1.6f;

            Vector3 e = eye.position;
            _maskMaterial.SetVector(SafeEyeId, new Vector4(e.x, e.y, e.z, 0f));
            _maskMaterial.SetFloat(SafeFloorYId, floorY);
            _maskMaterial.SetFloat(SafeRadiusId, safetyRadius);
            _maskMaterial.SetFloat(SafeNearKeepId, safetyNearKeep);
            _maskMaterial.SetFloat(SafeFloorClearanceId, safetyFloorClearance);
            _maskMaterial.SetFloat(SafeFloorNormalYId, safetyFloorNormalY);
            _maskMaterial.SetFloat(SafePunchId, Mathf.Max(safetyPunchMeters, invalidDepthMeters + 0.01f));
        }

        private bool EnsureSkyRing(RenderTexture raw)
        {
            for (int i = 0; i < _skyRing.Length; i++)
            {
                var rt = _skyRing[i];
                if (rt != null && rt.width == raw.width && rt.height == raw.height && rt.volumeDepth == raw.volumeDepth) continue;
                if (rt != null) { rt.Release(); Destroy(rt); }
                rt = new RenderTexture(raw.width, raw.height, 0, RenderTextureFormat.RFloat)
                {
                    name = "DepthMaskSkyHistory" + i,
                    dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                    volumeDepth = raw.volumeDepth,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (!rt.Create()) { LogSanitizeFailure("sky history texture could not be created"); return false; }
                _skyRing[i] = rt;
                _skyValid = 0;
            }
            return true;
        }

        // ------------------------------------------------------------------ live tuning

        private void PollTuneFile()
        {
            if (string.IsNullOrEmpty(_tunePath)) return;
            try
            {
                if (!System.IO.File.Exists(_tunePath)) return;
                var stamp = System.IO.File.GetLastWriteTimeUtc(_tunePath);
                if (stamp == _tuneStamp) return;
                _tuneStamp = stamp;
                var applied = new System.Text.StringBuilder();
                foreach (var rawLine in System.IO.File.ReadAllLines(_tunePath))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                    var f = typeof(DepthMaskCeiling).GetField(key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (f == null) { applied.Append(key).Append("=?unknown "); continue; }
                    object v = null;
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (f.FieldType == typeof(float) && float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out var fv)) v = fv;
                    else if (f.FieldType == typeof(int) && int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out var iv)) v = iv;
                    else if (f.FieldType == typeof(bool)) v = val == "1" || val.Equals("true", System.StringComparison.OrdinalIgnoreCase);
                    if (v == null) { applied.Append(key).Append("=?bad "); continue; }
                    f.SetValue(this, v);
                    applied.Append(key).Append('=').Append(v).Append(' ');
                }
                if (skyHistoryFrames < 0) skyHistoryFrames = 0;
                if (skyHistoryFrames > 2) skyHistoryFrames = 2;
                Debug.Log($"[DepthMaskCeiling] tune file applied: {applied}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DepthMaskCeiling] tune file unreadable: {e.Message}");
            }
        }

        private void LogSanitizeFailure(string why)
        {
            if (_sanitizeFailedLogged) return;
            _sanitizeFailedLogged = true;
            Debug.LogWarning($"[DepthMaskCeiling] invalid-depth sanitize OFF: {why}. Sky/glare will occlude again.");
        }

        private void PollFlagFile(bool force)
        {
            if (!force && Time.unscaledTime < _nextFlagPoll) return;
            _nextFlagPoll = Time.unscaledTime + 2f;
            if (string.IsNullOrEmpty(_flagPath)) { _flagFilePresent = false; return; }
            try { _flagFilePresent = System.IO.File.Exists(_flagPath); }
            catch { _flagFilePresent = false; }
            PollTuneFile();
        }

        private void EnsureDomeMesh()
        {
            if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
            if (_meshFilter == null) return;

            float h = EffectiveHeight;
            if (_domeMesh != null && _meshFilter.sharedMesh == _domeMesh &&
                h == _builtHeight && domeRadius == _builtRadius && planeRadius == _builtOuter &&
                azimuthSegments == _builtSegments && capRings == _builtRings)
                return;

            Mesh m = BuildDomeMesh(h, domeRadius, planeRadius, azimuthSegments, capRings);
            if (_domeMesh != null) Destroy(_domeMesh);
            _domeMesh = m;
            _meshFilter.sharedMesh = m;
            _builtHeight = h; _builtRadius = domeRadius; _builtOuter = planeRadius;
            _builtSegments = azimuthSegments; _builtRings = capRings;

            // Built in world metres: cancel whatever scale the authored plane carried.
            DepthCeiling.NeutralizeScale(transform);
            if (transform.parent == null) transform.localScale = Vector3.one;
        }

        private void Snap()
        {
            Transform h = ResolveHead();
            if (h == null) return;

            // The dome is centred ON the eye (the plane height is inside the mesh) and is
            // symmetric about the vertical, so only the position matters; yaw is kept only
            // because it is harmless. Never the head's pitch/roll — "up" must stay up.
            transform.SetPositionAndRotation(
                h.position,
                matchHeadYaw ? HeadYaw(h) : Quaternion.identity);
        }

        /// <summary>
        /// Distance from the eye to the dome mask surface along a ray at
        /// <paramref name="elevationRad"/> above the horizontal: max(R, h / sin(elev)).
        /// +Infinity at or below the horizon (the plane never gets there).
        /// </summary>
        public static float DomeDistance(float heightAboveEye, float radius, float elevationRad)
        {
            float s = Mathf.Sin(elevationRad);
            if (s <= 1e-6f) return float.PositiveInfinity;
            return Mathf.Max(radius, Mathf.Max(heightAboveEye, 0f) / s);
        }

        /// <summary>
        /// Build the dome mask in eye-centred metres (y up): a sphere cap of
        /// <paramref name="radius"/> above the plane y = <paramref name="heightAboveEye"/>,
        /// joined at horizontal radius sqrt(R² − h²) to a flat annulus reaching
        /// <paramref name="planeRadius"/>. Every triangle faces the eye (mask meshes render
        /// back-face culled). If h ≥ R the cap vanishes and it is a plain disc.
        /// </summary>
        public static Mesh BuildDomeMesh(float heightAboveEye, float radius, float planeRadius,
                                         int azimuthSegments = 64, int capRings = 12)
        {
            float h = Mathf.Max(0.01f, heightAboveEye);
            float R = Mathf.Max(0.01f, radius);
            int seg = Mathf.Clamp(azimuthSegments, 8, 256);
            int rings = Mathf.Clamp(capRings, 1, 64);

            bool hasCap = h < R;
            float r0 = hasCap ? Mathf.Sqrt(R * R - h * h) : 0f;      // seam radius
            float outer = Mathf.Max(planeRadius, r0 + 0.5f);
            float e0 = hasCap ? Mathf.Asin(h / R) : Mathf.PI * 0.5f; // seam elevation

            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();

            // Pole: the cap's top, or the disc centre when there is no cap.
            verts.Add(hasCap ? new Vector3(0f, R, 0f) : new Vector3(0f, h, 0f));
            int prevRingStart = -1; // -1 = the pole

            void AddRing(System.Func<float, Vector3> at)
            {
                int start = verts.Count;
                for (int j = 0; j < seg; j++)
                    verts.Add(at(j * Mathf.PI * 2f / seg));
                for (int j = 0; j < seg; j++)
                {
                    int jn = (j + 1) % seg;
                    if (prevRingStart < 0)
                        AddFacingEye(verts, tris, 0, start + j, start + jn);
                    else
                    {
                        AddFacingEye(verts, tris, prevRingStart + j, start + j, start + jn);
                        AddFacingEye(verts, tris, prevRingStart + j, start + jn, prevRingStart + jn);
                    }
                }
                prevRingStart = start;
            }

            if (hasCap)
            {
                for (int i = 1; i <= rings; i++)
                {
                    // Last ring lands exactly on the seam (y = h, r = r0).
                    float e = Mathf.Lerp(Mathf.PI * 0.5f, e0, i / (float)rings);
                    float y = i == rings ? h : R * Mathf.Sin(e);
                    float r = i == rings ? r0 : R * Mathf.Cos(e);
                    AddRing(a => new Vector3(r * Mathf.Sin(a), y, r * Mathf.Cos(a)));
                }
            }

            // Flat annulus: planar, so a few geometric rings are exact; they just keep
            // triangles from spanning 30 m in one hop.
            float rStart = Mathf.Max(r0, 0.05f);
            int planeRings = 4;
            for (int i = 1; i <= planeRings; i++)
            {
                float r = rStart * Mathf.Pow(outer / rStart, i / (float)planeRings);
                if (!hasCap && i == 1) r = Mathf.Min(r, rStart);
                AddRing(a => new Vector3(r * Mathf.Sin(a), h, r * Mathf.Cos(a)));
            }

            var mesh = new Mesh { name = "DepthMaskDome" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            // Generous bounds: the mesh is never culled by bounds in Meta's mask pass, but
            // keep them honest for anything that asks.
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Add a triangle wound so its face points at the eye (origin): Unity's front face
        /// normal is cross(b − a, c − a), the same convention DepthCeiling's reversed quad uses.
        /// </summary>
        private static void AddFacingEye(System.Collections.Generic.List<Vector3> v,
                                         System.Collections.Generic.List<int> t, int a, int b, int c)
        {
            Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            Vector3 toEye = -(v[a] + v[b] + v[c]) / 3f;
            if (Vector3.Dot(n, toEye) >= 0f) { t.Add(a); t.Add(b); t.Add(c); }
            else { t.Add(a); t.Add(c); t.Add(b); }
        }

        /// <summary>
        /// The head's rotation about the vertical axis alone.
        ///
        /// NOT Quaternion.Euler(0, head.eulerAngles.y, 0), which is the obvious way to
        /// write this and fails exactly where this plane earns its keep. That euler
        /// decomposition is ambiguous as pitch approaches vertical, so the extracted yaw
        /// flips and the plane spins under you at the moment you look straight up at it.
        ///
        /// Projecting forward onto the horizontal is stable everywhere except the poles,
        /// where forward IS vertical and projects to nothing — and there the head's own
        /// up vector is horizontal and stands in for it.
        /// </summary>
        private static Quaternion HeadYaw(Transform head)
        {
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f)
                flat = Vector3.ProjectOnPlane(head.up, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f)
                return Quaternion.identity;

            return Quaternion.LookRotation(flat.normalized, Vector3.up);
        }

        private Transform ResolveHead()
        {
            if (head != null) return head;
            if (_mainCamera == null) _mainCamera = Camera.main;
            return _mainCamera != null ? _mainCamera.transform : null;
        }
    }
}
