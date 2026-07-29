using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using XLua;

/// <summary>
/// XLua code-generation config for the DreamPark Lua surface.
///
/// ─────────────────────────────────────────────────────────────────────
///  READ THIS BEFORE TRIMMING THE LIST
/// ─────────────────────────────────────────────────────────────────────
///
/// This is NOT a security allowlist, and it is NOT a performance nicety.
/// For a platform whose content arrives OVER THE AIR, it is the RUNTIME API
/// CONTRACT: the set of Unity APIs a creator's Lua can actually call on a
/// device.
///
/// Three project settings combine to make that true:
///
///     managedStrippingLevel   Android: 3   iPhone: 3     (High)
///     stripEngineCode         1
///     il2cppCodeGeneration    Android: 1   iPhone: 1     (size-optimized)
///
/// IL2CPP strips managed code by STATIC REACHABILITY from the compiled app.
/// Creator Lua downloaded months after that build is invisible to the
/// analysis, so an API that no core C# happens to reference and that has no
/// generated wrapper is simply NOT IN THE BINARY — and no amount of runtime
/// reflection can find code that was stripped out. Listing a type here emits
/// a wrapper, and that wrapper is itself a static C# reference, which is what
/// keeps the API alive through stripping.
///
/// The asymmetry that should drive every decision here:
///
///     Over-include  →  a bigger binary and a longer IL2CPP build.
///     Under-include →  content that works perfectly in the SDK Editor,
///                      silently no-ops on Quest and iOS, and CANNOT BE
///                      FIXED BY SHIPPING CONTENT. It needs an app rebuild
///                      and a store release.
///
/// So this list is deliberately GENEROUS and deliberately ANTICIPATORY. It
/// covers what a game developer would reasonably reach for, not only what
/// today's content happens to call. Adding a type you turn out not to need
/// costs kilobytes. Omitting one costs a release cycle.
///
/// Security lives somewhere else entirely: LuaSecuritySandbox is a DENYLIST
/// on the type resolver, and it intentionally keeps CS.UnityEngine.* open
/// because real games need it. Registering a type here therefore widens no
/// attack surface — that surface is already reachable. All this changes is
/// whether the call is a fast, AOT-safe wrapper or a fragile reflection path.
///
/// WHY THE ZOMBIEZ BUG HAPPENED (July 2026): the list held 16 types and none
/// of them were UnityEngine.AI. Zombiez was the first content to drive a
/// NavMeshAgent from Lua. `AI.NavMesh.SamplePosition(pos, out NavMeshHit, …)`
/// — a struct passed by `out` on an un-generated type — is the classic shape
/// that survives Mono reflection in the Editor and fails under IL2CPP/AOT.
/// Every call site was wrapped in `pcall`, so it failed SILENTLY: zombies
/// animated and turned but never moved, and nothing reached the log.
///
/// ─────────────────────────────────────────────────────────────────────
///  MAINTENANCE
/// ─────────────────────────────────────────────────────────────────────
///
///  • Changes take effect only after DreamPark ▸ Troubleshooting ▸
///    Generate XLua Code, followed by a REBUILD. Generated output lands in
///    Assets/DreamPark/ThirdParty/XLua/Gen/ — commit that churn.
///  • This file is SDK-SYNCED. Any edit must be mirrored to
///    dreampark-sdk/Assets/DreamPark/Editor/DreamParkLuaConfig.cs or the two
///    projects disagree about what creators are allowed to call.
///  • If generation or the IL2CPP build fails on a specific type (obsolete
///    members, editor-only surface), remove that ONE entry and note why —
///    do not trim the list wholesale.
///  • Deliberately absent: Resources and PlayerPrefs. Content loading goes
///    through Addressables, and persistent per-user state goes through
///    GameStorageAPI — shared venue headsets must not accumulate local state.
///    Omitting them here does not block them (the sandbox decides that); it
///    just declines to bless them as a fast path.
/// </summary>
public static class DreamParkLuaConfig {

    [LuaCallCSharp]
    public static List<Type> LuaCallCSharp = new List<Type>() {

        // ── Math & core value types ──────────────────────────────────
        typeof(Vector2),
        typeof(Vector3),
        typeof(Vector4),
        typeof(Quaternion),
        typeof(Matrix4x4),
        typeof(Color),
        typeof(Color32),
        typeof(Bounds),
        typeof(Rect),
        typeof(Ray),
        typeof(Plane),
        typeof(Mathf),
        typeof(UnityEngine.Random),        // qualified: System.Random is also in scope

        // ── Engine/session basics ────────────────────────────────────
        typeof(Time),
        typeof(Application),
        typeof(Screen),
        typeof(SystemInfo),
        typeof(Debug),

        // ── Object model ─────────────────────────────────────────────
        typeof(UnityEngine.Object),        // qualified: System.Object is also in scope
        typeof(GameObject),
        typeof(Component),
        typeof(Behaviour),
        typeof(MonoBehaviour),
        typeof(Transform),
        typeof(RectTransform),
        typeof(LayerMask),

        // ── Physics ──────────────────────────────────────────────────
        // Physics.Raycast / OverlapSphere / SphereCast are the backbone of
        // grab, hit and proximity logic in hand-tracked content.
        typeof(Physics),
        typeof(RaycastHit),
        typeof(Rigidbody),
        typeof(Collider),
        typeof(BoxCollider),
        typeof(SphereCollider),
        typeof(CapsuleCollider),
        typeof(MeshCollider),
        typeof(CharacterController),
        typeof(Collision),
        typeof(ContactPoint),
        // PhysicsMaterial deliberately omitted for now: Unity 6 renamed
        // PhysicMaterial → PhysicsMaterial, nothing in either project
        // references it, and a wrong name breaks compilation in an
        // SDK-synced file. Add it once the name is confirmed in-editor.
        typeof(Joint),
        typeof(FixedJoint),
        typeof(HingeJoint),
        typeof(SpringJoint),
        // CharacterJoint was the one joint type missing, and it is the one
        // ragdolls need: creature_juice.lua.txt does
        // `part.go:AddComponent(typeof(UE.CharacterJoint))` and silently got
        // nothing back on device.
        typeof(CharacterJoint),

        // ── Navigation (the Zombiez gap) ─────────────────────────────
        typeof(UnityEngine.AI.NavMesh),
        typeof(UnityEngine.AI.NavMeshAgent),
        typeof(UnityEngine.AI.NavMeshHit),
        typeof(UnityEngine.AI.NavMeshObstacle),
        typeof(UnityEngine.AI.NavMeshPath),

        // ── Rendering ────────────────────────────────────────────────
        typeof(Renderer),
        typeof(MeshRenderer),
        typeof(SkinnedMeshRenderer),
        typeof(SpriteRenderer),
        typeof(LineRenderer),
        typeof(TrailRenderer),
        typeof(MeshFilter),
        typeof(Mesh),
        typeof(Material),
        typeof(MaterialPropertyBlock),
        typeof(Shader),
        typeof(Texture),
        typeof(Texture2D),
        typeof(Sprite),
        typeof(Camera),
        typeof(Light),

        // ── Animation ────────────────────────────────────────────────
        typeof(Animator),
        typeof(AnimatorCullingMode),   // zombie_ai sets animator.cullingMode
        typeof(AnimatorStateInfo),
        typeof(RuntimeAnimatorController),
        typeof(AnimationCurve),
        typeof(Keyframe),

        // ── Audio ────────────────────────────────────────────────────
        typeof(AudioSource),
        typeof(AudioClip),
        typeof(AudioListener),

        // ── Particles ────────────────────────────────────────────────
        // NOTE: ParticleSystem is the single most expensive entry here — it
        // drags in ~two dozen nested module structs. Kept anyway: "spawn an
        // effect" is table stakes for content, and discovering it was missing
        // would cost a store release.
        typeof(ParticleSystem),
        typeof(ParticleSystemRenderer),

        // ── UI & text ────────────────────────────────────────────────
        typeof(Canvas),
        typeof(CanvasGroup),
        typeof(UnityEngine.UI.Image),
        typeof(UnityEngine.UI.RawImage),
        typeof(UnityEngine.UI.Button),
        typeof(UnityEngine.UI.Slider),
        typeof(TMPro.TMP_Text),
        typeof(TMPro.TextMeshPro),
        typeof(TMPro.TextMeshProUGUI),

        // ── Coroutine yields ─────────────────────────────────────────
        typeof(YieldInstruction),
        typeof(WaitForSeconds),
        typeof(WaitForEndOfFrame),
        typeof(WaitForFixedUpdate),
        typeof(Coroutine),

        // ── Enums creator scripts pass as arguments ──────────────────
        // Enums are cheap to generate and are exactly the kind of thing that
        // gets discovered missing at the worst moment.
        typeof(Space),
        typeof(ForceMode),
        typeof(PrimitiveType),
        typeof(QueryTriggerInteraction),
        typeof(CollisionDetectionMode),
        typeof(RigidbodyConstraints),
        typeof(RigidbodyInterpolation),
        typeof(LightType),
        typeof(LightShadows),
        typeof(CameraClearFlags),
        typeof(TextureWrapMode),
        typeof(FilterMode),
        typeof(RenderMode),
        typeof(SendMessageOptions),
        // FindObjectsByType's second argument. Without the enum the argument
        // resolves to nil and the WHOLE call fails, not just the sort order.
        typeof(FindObjectsSortMode),
        typeof(FindObjectsInactive),
        typeof(HideFlags),
        typeof(RuntimePlatform),
        typeof(NetworkReachability),
        typeof(AudioRolloffMode),
        typeof(ParticleSystemStopBehavior),
        typeof(UnityEngine.AI.NavMeshPathStatus),
        typeof(UnityEngine.AI.ObstacleAvoidanceType),
        typeof(TMPro.TextAlignmentOptions),

        // ── DreamPark types creator Lua touches directly ─────────────
        // global:: because dreampark-core declares `class DreamPark` INSIDE
        // `namespace DreamPark`, so an unqualified `DreamPark.X` resolves to
        // the class and fails to compile (CS0117).
        typeof(global::DreamPark.PlayerRig),
        typeof(global::DreamPark.GameArea),
        typeof(global::DreamPark.LevelTemplate),
        typeof(global::DreamPark.AttractionTemplate),
        typeof(global::DreamPark.PropTemplate),
        typeof(global::DreamPark.MusicArea),
        // FloorAnchor rewrites localPosition every frame, so content that moves
        // an object vertically has to find and disable it — zombie_ai.lua.txt:296
        // does exactly that. It was the one DreamPark type referenced by shipped
        // content and missing here, and the surface scanner could not have caught
        // it either: BuildUnityTypeIndex only indexed UnityEngine assemblies until
        // July 2026.
        typeof(global::DreamPark.FloorAnchor),
        typeof(HandTracker),               // global namespace
    };

    /// <summary>
    /// Delegate signatures bridged from Lua functions into C# (LuaBehaviour /
    /// EasyLua callbacks, relay handlers, UnityEvent wiring). A delegate shape
    /// that isn't listed here cannot accept a Lua function on an AOT target.
    /// </summary>
    [CSharpCallLua]
    public static List<Type> CSharpCallLua = new List<Type>() {
        typeof(Action),
        typeof(Action<bool>),
        typeof(Action<int>),
        typeof(Action<float>),
        typeof(Action<string>),
        typeof(Action<GameObject>),
        typeof(Action<Transform>),
        typeof(Action<Collider>),
        typeof(Action<Collision>),
        typeof(Action<Vector3>),
        typeof(Func<bool>),
        typeof(UnityEngine.Events.UnityAction),
        typeof(UnityEngine.Events.UnityAction<bool>),
        typeof(UnityEngine.Events.UnityAction<float>),
    };

    /// <summary>
    /// Pure value types worth GC-optimizing so pushing them across the Lua
    /// boundary doesn't allocate.
    ///
    /// ⚠ THIS LIST MUST NOT REPEAT ANYTHING XLUA ALREADY REGISTERS.
    ///
    /// GCOptimize entries land in a Dictionary via OptimizeCfg.Add()
    /// (Generator.cs:1320) — NOT a List that gets .Distinct()ed like
    /// LuaCallCSharp does. So a type declared in two [GCOptimize] sources
    /// throws during generation and takes the ENTIRE run down:
    ///
    ///     ArgumentException: An item with the same key has already been
    ///     added. Key: UnityEngine.Vector2
    ///
    /// XLua's own XLua.SysGenConfig (Src/GenAttributes.cs:131, active whenever
    /// XLUA_GENERAL is not defined — i.e. always, in Unity) already covers:
    ///
    ///     Vector2, Vector3, Vector4, Color, Quaternion, Ray, Bounds, Ray2D
    ///
    /// Those are handled. Listing them here does not "make them more
    /// optimized", it just breaks codegen. This list therefore holds ONLY the
    /// pure value types XLua does not already claim.
    ///
    /// (This is not hypothetical: the pre-July-2026 version of this file
    /// repeated Vector2/Vector3/Quaternion/Color, so Generate XLua Code threw
    /// every time it was run and NO wrappers were ever produced — which is why
    /// Gen/ sat at 12 files with zero UnityEngine coverage.)
    ///
    /// Entries must also be structs whose fields are ALL value types — XLua's
    /// requirement, and violating it fails confusingly. RaycastHit and
    /// NavMeshHit are deliberately absent: RaycastHit carries a Collider
    /// reference, and NavMeshHit is produced about once per pathing call, so
    /// the allocation is irrelevant next to the risk.
    /// </summary>
    [GCOptimize]
    public static List<Type> GCOptimize = new List<Type>() {
        typeof(Color32),
        typeof(Rect),
        typeof(Keyframe),
    };

    // ─────────────────────────────────────────────────────────────────
    //  EDITOR-ONLY / PLATFORM-ONLY MEMBER FILTER
    // ─────────────────────────────────────────────────────────────────
    //
    //  Codegen runs in the EDITOR, where Unity's managed API includes members
    //  that do not exist in a player build. XLua cannot tell the difference,
    //  so it happily emits bindings for them and the PLAYER build then fails:
    //
    //      UnityEngine_MaterialWrap.cs(1220,57): error CS1061:
    //      'Material' does not contain a definition for 'IsChildOf'
    //
    //  Generation succeeds; the build breaks. Two families are responsible:
    //
    //    • Editor-only surface — Material Variants (parent/isVariant/property
    //      overrides + locks), MeshRenderer lightmap authoring, Light shadow
    //      authoring, Texture.imageContentsHash. All exist only in-editor.
    //    • Platform-only surface — AudioSource's gamepad-speaker API is PS5,
    //      and UnityEngine.GamepadSpeakerOutputType isn't in the Android/iOS
    //      player at all.
    //
    //  Filtered with a predicate rather than XLua's List<List<string>>
    //  BlackList because that form matches on an exact parameter signature —
    //  one entry per overload, and a two-element entry only ever matches a
    //  zero-argument method. A Func<MemberInfo,bool> is consulted by BOTH
    //  isMemberInBlackList and isMethodInBlackList (Generator.cs:530, :557),
    //  so one name covers every overload, the property, and its accessors.
    //  memberFilters is a LIST (Generator.cs:1410), so this coexists with
    //  ExampleConfig's existing filter instead of replacing it.
    //
    //  TO EXTEND: if a future build fails with another CS1061/CS0117 out of
    //  Gen/, add the member name here and regenerate. Unity caps the reported
    //  error count, so a second pass can surface more than the first.
    // ─────────────────────────────────────────────────────────────────

    /// Editor-only MonoBehaviour messages. Banned on EVERY type rather than
    /// per-class: they are declared on a base (LevelTemplate) but re-emitted
    /// into each subclass wrapper, and creator Lua has no business invoking
    /// an editor gizmo callback regardless of who declared it.
    private static readonly HashSet<string> EditorOnlyMessages = new HashSet<string>(StringComparer.Ordinal) {
        "OnValidate",
        "OnDrawGizmos",
        "OnDrawGizmosSelected",
    };

    /// Members that exist in the Editor (or on another platform) but not in
    /// an Android/iOS player. Keyed by declaring type's full name.
    private static readonly Dictionary<string, HashSet<string>> EditorOnlyMembers =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) {

        // Material Variants — editor-authoring API.
        { "UnityEngine.Material", new HashSet<string>(StringComparer.Ordinal) {
            "parent", "isVariant", "IsChildOf",
            "RevertAllPropertyOverrides", "IsPropertyOverriden",
            "IsPropertyLocked", "IsPropertyLockedByAncestor", "SetPropertyLock",
            "ApplyPropertyOverride", "RevertPropertyOverride",
        }},

        // Lightmap authoring — editor-only.
        { "UnityEngine.MeshRenderer", new HashSet<string>(StringComparer.Ordinal) {
            "scaleInLightmap", "receiveGI", "stitchLightmapSeams",
        }},

        // Shadow/light authoring — editor-only.
        { "UnityEngine.Light", new HashSet<string>(StringComparer.Ordinal) {
            "SetLightDirty", "shadowRadius", "shadowAngle",
        }},

        { "UnityEngine.Texture", new HashSet<string>(StringComparer.Ordinal) {
            "imageContentsHash",
        }},

        // PS5 gamepad-speaker output — absent from Android/iOS players.
        { "UnityEngine.AudioSource", new HashSet<string>(StringComparer.Ordinal) {
            "PlayOnGamepad", "DisableGamepadOutput",
            "SetGamepadSpeakerMixLevel", "SetGamepadSpeakerMixLevelDefault",
            "SetGamepadSpeakerRestrictedAudio",
            "GamepadSpeakerSupportsOutputType", "gamepadSpeakerOutputType",
        }},

        { "UnityEngine.ParticleSystemRenderer", new HashSet<string>(StringComparer.Ordinal) {
            "supportsMeshInstancing",
        }},
    };

    [BlackList]
    public static Func<MemberInfo, bool> EditorOnlyMemberFilter = (memberInfo) =>
    {
        if (memberInfo == null) return false;

        if (EditorOnlyMessages.Contains(memberInfo.Name)) return true;

        Type declaring = memberInfo.DeclaringType;
        if (declaring == null) return false;

        // Nothing under UnityEditor can ship in a player, ever.
        string ns = declaring.Namespace;
        if (ns != null && (ns == "UnityEditor" || ns.StartsWith("UnityEditor.", StringComparison.Ordinal)))
            return true;

        HashSet<string> banned;
        return EditorOnlyMembers.TryGetValue(declaring.FullName ?? "", out banned)
               && banned.Contains(memberInfo.Name);
    };
}
