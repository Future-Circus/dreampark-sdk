# C# → XLua Feasibility Report

Scanned **123** scripts.  ✅ CLEAN: **4**  🟡 CLEAN_WARN: **16**  🔴 KEEP_CSHARP: **91**  🟠 NEEDS_RUNTIME: **12**

## ✅ CLEAN — `Assets/DreamPark/Scripts/Core/CollideAudio.cs`
*class `CollideAudio` : `MonoBehaviour`, 27 LOC*

- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Mathf, Random
- **INFO** `field-maps` (L7): AudioClip audioClip → audioClipInjections

## ✅ CLEAN — `Assets/DreamPark/Scripts/Core/OptimizedAF/OptimizedAFIgnore.cs`
*class `OptimizedAFIgnore` : `MonoBehaviour`, 10 LOC*

- No findings — direct translation candidate.

## ✅ CLEAN — `Assets/DreamPark/Scripts/Features/Net/NetScope.cs`
*class `NetScope` : `MonoBehaviour`, 25 LOC*

- **INFO** `field-maps` (L22): string scopeKey → stringInjections

## ✅ CLEAN — `Assets/DreamPark/Scripts/Utils/Simulator.cs`
*class `Simulator` : `MonoBehaviour`, 186 LOC*

- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Compatibility/Meta/PassthroughCameraRouter.cs`
*class `PassthroughCameraRouter` : `MonoBehaviour`, 35 LOC*

- **WARN** `field-unknown-type` (L8): PassthroughCameraAccess cameraAccess — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `field-maps` (L6): Renderer targetRenderer → componentInjections
- **INFO** `field-maps` (L7): string texturePropertyName → stringInjections

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Core/OptimizedAF/FPSDisplay.cs`
*class `FPSDisplay` : `MonoBehaviour`, 35 LOC*

- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Camera — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Time

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Attractions/GameArea.cs`
*class `GameArea` : `MonoBehaviour`, 357 LOC*

- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Camera — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug, Mathf
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions
- **INFO** `field-maps` (L38): string gameId → stringInjections
- **INFO** `field-maps` (L39): string resourceName → stringInjections
- **INFO** `field-maps` (L41): int priority → intInjections
- **INFO** `field-maps` (L42): bool isPlaying → boolInjections
- **INFO** `field-maps` (L47): float padding → floatInjections
- **INFO** `field-maps` (L51): bool showPaddingGizmo → boolInjections
- **INFO** `field-maps` (L57): Vector3 halfExtents → vector3Injections
- **INFO** `field-maps` (L64): Vector3 unpaddedHalfExtents → vector3Injections
- **INFO** `field-maps` (L69): Color zoneColor → colorInjections

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Attractions/RealisticRolloff.cs`
*class `RealisticRolloff` : `MonoBehaviour`, 46 LOC*

- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: AnimationCurve — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `field-maps` (L7): bool DestroyOnStart → boolInjections

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyCall.cs`
*class `EasyCall` : `EasyEvent`, 112 LOC*

- **WARN** `unknown-on-method` (L58): OnEvent() — if this is a Unity message it has no relay; if a plain method, ignore
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `field-maps` (L55): MonoBehaviour target → componentInjections
- **INFO** `field-maps` (L56): string methodName → stringInjections

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyEvent.cs`
*class `EasyEvent` : `MonoBehaviour`, 271 LOC*

- **WARN** `field-unknown-type` (L11): UnityEvent<object> onEvent — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L12): EasyEvent aboveEvent — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L13): EasyEvent belowEvent — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `unknown-on-method` (L29): OnEvent() — if this is a Unity message it has no relay; if a plain method, ignore
- **WARN** `unknown-on-method` (L35): OnEventDisable() — if this is a Unity message it has no relay; if a plain method, ignore
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Application, Time
- **INFO** `field-maps` (L14): bool eventOnStart → boolInjections
- **INFO** `field-maps` (L15): bool isEnabled → boolInjections
- **INFO** `field-maps` (L16): float lastDisabledAtRealtime → floatInjections
- **INFO** `editor-only-message` (L49): OnValidate() is editor-only — dropped in translation

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/EasyEvent/EasySpawn.cs`
*class `EasySpawn` : `EasyEvent`, 74 LOC*

- **WARN** `unknown-on-method` (L20): OnEvent() — if this is a Unity message it has no relay; if a plain method, ignore
- **WARN** `unknown-on-method` (L38): OnSpawnedObjectDestroyed() — if this is a Unity message it has no relay; if a plain method, ignore
- **WARN** `field-unknown-type` (L68): System.Action onDestroyed — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `field-maps` (L5): GameObject spawnPrefab → injections (GameObject)
- **INFO** `field-maps` (L6): Transform spawnPoint → transformInjections
- **INFO** `field-maps` (L7): int amount → intInjections
- **INFO** `field-maps` (L8): bool copyRotation → boolInjections
- **INFO** `field-maps` (L9): bool waitForDestroyed → boolInjections

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Entity/AnimationEventRouter.cs`
*class `AnimationEventRouter` : `MonoBehaviour`, 54 LOC*

- **WARN** `field-unknown-type` (L12): EasyAnimationEvent linkedEvent — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `field-maps` (L11): GameObject target → injections (GameObject)

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Lua/LuaMessageRelays.cs`
*class `LuaVoidRelay` : `MonoBehaviour`, 125 LOC*

- **WARN** `field-unknown-type` (L105): Action cb — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L106): Action<Collider> cb — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L107): Action<Collision> cb — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L108): Action<bool> cb — unknown type; verify an injection slot exists or pass via script injection

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Net/DreamBoxClient.cs`
*class `DreamBoxClient` : `MonoBehaviour`, 637 LOC*

- **WARN** `field-unknown-type` (L91): NetId _testNetId — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `event-subscription` (L167): Subscribes to event 'OnSessionPaired' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `event-subscription` (L262): Subscribes to event 'OnRelayDiscovered' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `unknown-on-method` (L277): OnRelayDiscovered() — if this is a Unity message it has no relay; if a plain method, ignore
- **WARN** `unknown-on-method` (L367): OnSessionPaired() — if this is a Unity message it has no relay; if a plain method, ignore
- **WARN** `event-subscription` (L386): Subscribes to event 'PeerConnectedEvent' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `event-subscription` (L397): Subscribes to event 'PeerDisconnectedEvent' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `event-subscription` (L424): Subscribes to event 'NetworkReceiveEvent' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `event-subscription` (L458): Subscribes to event 'NetworkErrorEvent' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug, Mathf, Time
- **INFO** `field-maps` (L73): string serverIP → stringInjections
- **INFO** `field-maps` (L74): int serverPort → intInjections
- **INFO** `field-maps` (L75): string connectionKey → stringInjections
- **INFO** `field-maps` (L78): bool autoReconnect → boolInjections
- **INFO** `field-maps` (L79): float reconnectBaseDelay → floatInjections
- **INFO** `field-maps` (L80): float reconnectMaxDelay → floatInjections
- **INFO** `field-maps` (L81): int maxReconnectAttempts → intInjections
- **INFO** `field-maps` (L85): bool connectOnStart → boolInjections
- **INFO** `field-maps` (L89): bool verboseNetLogs → boolInjections
- **INFO** `field-maps` (L98): int Ping → intInjections
- **INFO** `field-maps` (L99): int RTT → intInjections
- **INFO** `field-maps` (L101): string ServerAddress → stringInjections

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Net/NetId.cs`
*class `NetId` : `MonoBehaviour`, 136 LOC*

- **WARN** `field-unknown-type` (L29): uint explicitId — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Player/BodyTracker.cs`
*class `BodyTracker` : `MonoBehaviour`, 51 LOC*

- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Camera — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **INFO** `field-maps` (L11): float yOffset → floatInjections

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Player/FeetTracker.cs`
*class `FeetTracker` : `MonoBehaviour`, 71 LOC*

- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Camera — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Mathf
- **INFO** `field-maps` (L10): Transform head → transformInjections
- **INFO** `field-maps` (L11): Rigidbody rb → componentInjections
- **INFO** `field-maps` (L12): float yOffset → floatInjections
- **INFO** `field-maps` (L13): float kickMultiplier → floatInjections
- **INFO** `editor-only-message` (L62): OnDrawGizmos() is editor-only — dropped in translation

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Features/Player/HeadTracker.cs`
*class `HeadTracker` : `MonoBehaviour`, 48 LOC*

- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Camera — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **INFO** `field-maps` (L10): Transform head → transformInjections
- **INFO** `field-maps` (L11): Rigidbody rb → componentInjections

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Utils/Componentizer.cs`
*class `Componentizer` : `MonoBehaviour`, 107 LOC*

- **WARN** `unityevent-listener` (L104): UnityEvent.AddListener — verify the event's arg types map to a supported delegate signature
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Application

## 🟡 CLEAN_WARN — `Assets/DreamPark/Scripts/Utils/MeshGizmoRenderer.cs`
*class `MeshGizmoRenderer` : `MonoBehaviour`, 27 LOC*

- **WARN** `field-unknown-type` (L5): Mesh mesh — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `field-maps` (L6): Color color → colorInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Core/Reset.cs`
*class `Reset` : `MonoBehaviour`, 27 LOC*

- **RUNTIME** `unsupported-message` (L19): OnApplicationQuit() has no Lua relay — needs a new relay class + delegate sig in DreamParkLuaConfig
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Application

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/Attractions/FloorCutout.cs`
*class `FloorCutout` : `MonoBehaviour`, 108 LOC*

- **RUNTIME** `no-injection-type` (L99): List<Vector3> points — only GameObject[] lists are injectable
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `field-maps` (L100): Transform ogPosition → transformInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/Attractions/LevelTemplate.cs`
*class `LevelTemplate` : `MonoBehaviour`, 736 LOC*

- **RUNTIME** `no-injection-type` (L91): enum GameLevelSize size — no enum injection; use intInjections + named constants
- **RUNTIME** `no-injection-type` (L92): Vector2 customSize — no injection slot for Vector2; add an injection type or encode as floats/string
- **RUNTIME** `no-injection-type` (L93): Vector2 defaultAnchorPosition — no injection slot for Vector2; add an injection type or encode as floats/string
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: LayerMask, LineRenderer, Resources — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `field-unknown-type` (L110): JSONObject floorData — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Application, Debug, Mathf
- **INFO** `require-component`: [RequireComponent] — LuaBehaviour won't auto-add; converter must add components to the prefab explicitly
- **INFO** `field-maps` (L90): string gameId → stringInjections
- **INFO** `field-maps` (L94): bool generateFloor → boolInjections
- **INFO** `field-maps` (L95): bool generateCeiling → boolInjections
- **INFO** `field-maps` (L96): GameObject runtimePlane → injections (GameObject)
- **INFO** `field-maps` (L97): GameObject runtimeCeiling → injections (GameObject)
- **INFO** `field-maps` (L100): bool renderDimensions → boolInjections
- **INFO** `field-maps` (L101): bool showCutoutGizmos → boolInjections
- **INFO** `field-maps` (L104): bool showGridGizmo → boolInjections
- **INFO** `field-maps` (L105): int gridDensity → intInjections
- **INFO** `field-maps` (L106): float gridWidth → floatInjections
- **INFO** `field-maps` (L107): float gridHeight → floatInjections
- **INFO** `field-maps` (L108): int gridX → intInjections
- **INFO** `field-maps` (L109): int gridY → intInjections
- **INFO** `field-maps` (L111): Material floorMaterial → materialInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/Attractions/MusicArea.cs`
*class `MusicArea` : `MonoBehaviour`, 294 LOC*

- **RUNTIME** `coroutine` (L262): Coroutines — no Lua coroutine bridge wired up; rewrite as update() state machine or add util.cs_generator bridge
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Camera — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `field-unknown-type` (L21): MusicArea currentMusicArea — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Mathf, Time
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions
- **INFO** `field-maps` (L19): int activeMusicAreas → intInjections
- **INFO** `field-maps` (L47): AudioClip musicTrack → audioClipInjections
- **INFO** `field-maps` (L48): float volume → floatInjections
- **INFO** `field-maps` (L49): int priority → intInjections
- **INFO** `field-maps` (L50): bool isPlaying → boolInjections
- **INFO** `field-maps` (L57): bool isFocused → boolInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/Attractions/PropTemplate.cs`
*class `PropTemplate` : `MonoBehaviour`, 420 LOC*

- **RUNTIME** `no-injection-type` (L28): enum PropCategory category — no enum injection; use intInjections + named constants
- **RUNTIME** `no-injection-type` (L34): Vector2 customFootprintMeters — no injection slot for Vector2; add an injection type or encode as floats/string
- **RUNTIME** `no-injection-type` (L35): Vector2 footprintOffsetMeters — no injection slot for Vector2; add an injection type or encode as floats/string
- **RUNTIME** `unsupported-message` (L113): OnTransformChildrenChanged() has no Lua relay — needs a new relay class + delegate sig in DreamParkLuaConfig
- **WARN** `field-unknown-type` (L37): JSONObject pointData — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Application, Mathf
- **INFO** `require-component`: [RequireComponent] — LuaBehaviour won't auto-add; converter must add components to the prefab explicitly
- **INFO** `field-maps` (L26): string gameId → stringInjections
- **INFO** `field-maps` (L27): string resourceName → stringInjections
- **INFO** `field-maps` (L30): bool affectsGapFiller → boolInjections
- **INFO** `field-maps` (L32): bool cutGapFillerHole → boolInjections
- **INFO** `field-maps` (L33): bool useColliderBounds → boolInjections
- **INFO** `field-maps` (L36): bool showFootprintGizmos → boolInjections
- **INFO** `field-maps` (L38): GameObject runtimePlane → injections (GameObject)
- **INFO** `field-maps` (L47): float SurfaceHeight → floatInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyCounter.cs`
*class `EasyCounter` : `EasyEvent`, 134 LOC*

- **RUNTIME** `no-injection-type` (L43): enum VariableType variableType — no enum injection; use intInjections + named constants
- **RUNTIME** `no-injection-type` (L46): enum VariableType totalCountSource — no enum injection; use intInjections + named constants
- **WARN** `field-unknown-type` (L52): EasyEvent onNotEqual — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `unknown-on-method` (L114): OnEvent() — if this is a Unity message it has no relay; if a plain method, ignore
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions
- **INFO** `field-maps` (L44): int count → intInjections
- **INFO** `field-maps` (L45): string variableName → stringInjections
- **INFO** `field-maps` (L47): int totalCount → intInjections
- **INFO** `field-maps` (L48): string totalCountVariableName → stringInjections
- **INFO** `field-maps` (L49): int runtimeTotalCountValue → intInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyDisable.cs`
*class `EasyDisable` : `EasyEvent`, 121 LOC*

- **RUNTIME** `no-injection-type` (L14): enum DisableType disableType — no enum injection; use intInjections + named constants
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Animator — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `unknown-on-method` (L33): OnEvent() — if this is a Unity message it has no relay; if a plain method, ignore
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `field-maps` (L16): GameObject gameObjectToDisable → injections (GameObject)
- **INFO** `field-maps` (L17): MonoBehaviour componentToDisable → componentInjections
- **INFO** `field-maps` (L19): GameObject componentsTarget → injections (GameObject)
- **INFO** `field-maps` (L20): bool disableRenderers → boolInjections
- **INFO** `field-maps` (L21): bool disableColliders → boolInjections
- **INFO** `field-maps` (L22): bool disableRigidbodies → boolInjections
- **INFO** `field-maps` (L23): bool disableBehaviours → boolInjections
- **INFO** `field-maps` (L25): GameObject childrenParent → injections (GameObject)
- **INFO** `field-maps` (L27): GameObject childrenComponentsParent → injections (GameObject)
- **INFO** `field-maps` (L28): bool childDisableRenderers → boolInjections
- **INFO** `field-maps` (L29): bool childDisableColliders → boolInjections
- **INFO** `field-maps` (L30): bool childDisableRigidbodies → boolInjections
- **INFO** `field-maps` (L31): bool childDisableBehaviours → boolInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyEnable.cs`
*class `EasyEnable` : `EasyEvent`, 128 LOC*

- **RUNTIME** `no-injection-type` (L14): enum EnableType enableType — no enum injection; use intInjections + named constants
- **WARN** `unknown-on-method` (L40): OnEvent() — if this is a Unity message it has no relay; if a plain method, ignore
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `field-maps` (L17): bool enable → boolInjections
- **INFO** `field-maps` (L20): bool toggleOnNext → boolInjections
- **INFO** `field-maps` (L22): GameObject gameObjectToEnable → injections (GameObject)
- **INFO** `field-maps` (L23): MonoBehaviour componentToEnable → componentInjections
- **INFO** `field-maps` (L24): Animator animatorToEnable → componentInjections
- **INFO** `field-maps` (L26): GameObject componentsTarget → injections (GameObject)
- **INFO** `field-maps` (L27): bool enableRenderers → boolInjections
- **INFO** `field-maps` (L28): bool enableColliders → boolInjections
- **INFO** `field-maps` (L29): bool enableRigidbodies → boolInjections
- **INFO** `field-maps` (L30): bool enableBehaviours → boolInjections
- **INFO** `field-maps` (L32): GameObject childrenParent → injections (GameObject)
- **INFO** `field-maps` (L34): GameObject childrenComponentsParent → injections (GameObject)
- **INFO** `field-maps` (L35): bool childEnableRenderers → boolInjections
- **INFO** `field-maps` (L36): bool childEnableColliders → boolInjections
- **INFO** `field-maps` (L37): bool childEnableRigidbodies → boolInjections
- **INFO** `field-maps` (L38): bool childEnableBehaviours → boolInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/Entity/Interactable.cs`
*class `Interactable` : `MonoBehaviour`, 367 LOC*

- **RUNTIME** `no-injection-type` (L190): string[] layers — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L191): string[] tags — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L196): InteractionFilter[] interactionFilters — only GameObject[] lists are injectable
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: LayerMask — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `field-unknown-type` (L87): RigidbodyInterpolation interpolation — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L88): CollisionDetectionMode collisionDetectionMode — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L89): RigidbodyConstraints constraints — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L192): UnityEvent<CollisionWrapper> onInteractionEnter — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L193): UnityEvent<CollisionWrapper> onInteractionStay — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L194): UnityEvent<CollisionWrapper> onInteractionExit — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `field-maps` (L84): float mass → floatInjections
- **INFO** `field-maps` (L85): bool useGravity → boolInjections
- **INFO** `field-maps` (L86): bool isKinematic → boolInjections
- **INFO** `field-maps` (L245): bool debugger → boolInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Features/Player/HandTracker.cs`
*class `HandTracker` : `MonoBehaviour`, 293 LOC*

- **RUNTIME** `no-injection-type` (L27): enum HandPreference handPreference — no enum injection; use intInjections + named constants
- **WARN** `invoke-timer` (L177): Invoke/InvokeRepeating — translate to timer accumulation in update()
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions
- **INFO** `field-maps` (L29): bool flipVisual → boolInjections
- **INFO** `field-maps` (L31): Vector3 flipVisualRotation → vector3Injections
- **INFO** `field-maps` (L33): Vector3 flipVisualPosition → vector3Injections
- **INFO** `field-maps` (L41): float enableDelay → floatInjections

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Utils/CoroutineRunner.cs`
*class `CoroutineRunner` : `MonoBehaviour`, 22 LOC*

- **RUNTIME** `coroutine` (L19): Coroutines — no Lua coroutine bridge wired up; rewrite as update() state machine or add util.cs_generator bridge
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions

## 🟠 NEEDS_RUNTIME — `Assets/DreamPark/Scripts/Utils/DepthMask.cs`
*class `DepthMask` : `MonoBehaviour`, 96 LOC*

- **RUNTIME** `no-injection-type` (L9): List<MeshFilter> myMeshFilters — only GameObject[] lists are injectable
- **INFO** `field-maps` (L10): float _someOffsetFloatValue → floatInjections

## 🔴 KEEP_CSHARP — `Assets/Content/YOUR_GAME_HERE/Scripts/CoreExtensionsInterface.cs`
*class `CoreExtensionsInterface` : `—`, 59 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/AnimationExtensions.cs`
*class `AnimationExtensions` : `—`, 59 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/AuthAPI.cs`
*class `AuthAPI` : `MonoBehaviour`, 343 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/ContentAPI.cs`
*class `UploadedFileRecord` : `—`, 2186 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/DreamParkAPI.cs`
*class `DreamParkAPI` : `—`, 247 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/FailedBundleStore.cs`
*class `FailedBundleStore` : `—`, 221 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/GameStorageAPI.cs`
*class `GameStorageAPI` : `—`, 870 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/OptimizedAF/LevelObjectManager.cs`
*class `LevelObjectManager` : `MonoBehaviour`, 556 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **RUNTIME** `no-injection-type` (L12): ColliderSettings[] colliders — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L13): RigidbodySettings[] rigidbodies — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L14): ComponentSettings[] components — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L15): Animator[] animators — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L16): RendererSettings[] renderers — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L17): ParticleSystemSettings[] particleSystems — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L466): List<LevelObject> levelObjects — only GameObject[] lists are injectable
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: LayerMask — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `field-unknown-type` (L70): Joint joint — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Mathf
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions
- **INFO** `field-maps` (L11): GameObject gameObject → injections (GameObject)
- **INFO** `field-maps` (L18): bool isRendererDisabled → boolInjections
- **INFO** `field-maps` (L19): bool enabled → boolInjections
- **INFO** `field-maps` (L20): string name → stringInjections
- **INFO** `field-maps` (L21): string tag → stringInjections
- **INFO** `field-maps` (L22): int layer → intInjections
- **INFO** `field-maps` (L23): bool isPriority → boolInjections
- **INFO** `field-maps` (L24): bool ignoreOptimization → boolInjections
- **INFO** `field-maps` (L69): Rigidbody rigidbody → componentInjections
- **INFO** `field-maps` (L71): bool isKinematic → boolInjections
- **INFO** `field-maps` (L72): bool detectCollisions → boolInjections
- **INFO** `field-maps` (L73): bool useGravity → boolInjections
- **INFO** `field-maps` (L74): Vector3 linearVelocity → vector3Injections
- **INFO** `field-maps` (L75): Vector3 angularVelocity → vector3Injections
- **INFO** `field-maps` (L140): Collider collider → componentInjections
- **INFO** `field-maps` (L141): bool enabled → boolInjections
- **INFO** `field-maps` (L158): ParticleSystem particleSystem → componentInjections
- **INFO** `field-maps` (L159): bool enabled → boolInjections
- **INFO** `field-maps` (L181): Component component → componentInjections
- **INFO** `field-maps` (L182): bool enabled → boolInjections
- **INFO** `field-maps` (L220): Renderer renderer → componentInjections
- **INFO** `field-maps` (L225): bool enabled → boolInjections
- **INFO** `field-maps` (L226): bool enableOptimizedMaterial → boolInjections
- **INFO** `field-maps` (L465): bool gatherChildren → boolInjections

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/OptimizedAF/OptimizationSettings.cs`
*class `OptimizationSettings` : `ScriptableObject`, 22 LOC*

- **BLOCKER** `scriptable-object`: ScriptableObject — no Lua equivalent; keep as C# data

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/OptimizedAF/OptimizedAF.cs`
*class `OptimizedAF` : `MonoBehaviour`, 130 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Camera — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `field-unknown-type` (L10): OptimizationSettings settings — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Application, Debug
- **INFO** `field-maps` (L11): bool showFPS → boolInjections
- **INFO** `field-maps` (L12): bool showGizmos → boolInjections
- **INFO** `field-maps` (L13): bool disable → boolInjections
- **INFO** `editor-only-message` (L109): OnDrawGizmos() is editor-only — dropped in translation

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/ProfileAPI.cs`
*class `ProfileItem` : `—`, 1135 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/SDKAPI.cs`
*class `SDKAPI` : `—`, 99 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Core/SDKVersion.cs`
*class `SDKVersion` : `—`, 90 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Attractions/AttractionTemplate.cs`
*class `AttractionTemplate` : `LevelTemplate`, 10 LOC*

- **BLOCKER** `sdk-base-class`: Inherits SDK type 'LevelTemplate' — SDK component, not creator gameplay code

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Attractions/CalibrateLevel.cs`
*class `CalibrateLevel` : `MonoBehaviour`, 450 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **RUNTIME** `no-injection-type` (L17): LayerMask arMeshLayer — no injection slot for LayerMask; add an injection type or encode as floats/string
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: Camera, LayerMask, MeshCollider, MeshFilter — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `field-unknown-type` (L12): ARMeshManager arMeshManager — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L27): LevelTemplate levelTemplate — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L28): JSONObject floorData — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `event-subscription` (L425): Subscribes to event 'meshesChanged' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `unknown-on-method` (L434): OnMeshesChanged() — if this is a Unity message it has no relay; if a plain method, ignore
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Application, Debug, Mathf, Physics, Time
- **INFO** `require-component`: [RequireComponent] — LuaBehaviour won't auto-add; converter must add components to the prefab explicitly
- **INFO** `field-maps` (L15): float updateInterval → floatInjections
- **INFO** `field-maps` (L16): float surfaceFollowSpeed → floatInjections
- **INFO** `field-maps` (L24): bool calibrated → boolInjections
- **INFO** `field-maps` (L25): bool EditorOverride → boolInjections

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Attractions/CalibrateProp.cs`
*class `CalibrateProp` : `MonoBehaviour`, 106 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **RUNTIME** `no-injection-type` (L11): LayerMask arMeshLayer — no injection slot for LayerMask; add an injection type or encode as floats/string
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: LayerMask — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `field-unknown-type` (L16): PropTemplate propTemplate — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L17): JSONObject pointData — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Mathf, Physics, Time
- **INFO** `field-maps` (L10): float updateInterval → floatInjections
- **INFO** `field-maps` (L12): float raycastHeight → floatInjections
- **INFO** `field-maps` (L13): float raycastLength → floatInjections
- **INFO** `field-maps` (L18): bool calibrated → boolInjections
- **INFO** `field-maps` (L19): bool EditorOverride → boolInjections

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Attractions/FloorAnchor.cs`
*class `FloorAnchor` : `MonoBehaviour`, 920 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **BLOCKER** `execute-in-editmode` (L65): ExecuteInEditMode/ExecuteAlways — LuaBehaviour only runs in play mode
- **RUNTIME** `coroutine` (L245): Coroutines — no Lua coroutine bridge wired up; rewrite as update() state machine or add util.cs_generator bridge
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: MeshFilter — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `field-unknown-type` (L71): CalibrateLevel calibrator — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `event-subscription` (L249): Subscribes to event 'OnAnyLevelTemplateChanged' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `unknown-on-method` (L257): OnCalibrationChanged() — if this is a Unity message it has no relay; if a plain method, ignore
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug, Mathf, Time
- **INFO** `field-maps` (L69): MeshFilter floorMeshFilter → componentInjections
- **INFO** `field-maps` (L70): Transform floorTransform → transformInjections
- **INFO** `field-maps` (L74): Vector3 localOffset → vector3Injections
- **INFO** `field-maps` (L75): bool autoFindFloor → boolInjections
- **INFO** `field-maps` (L76): bool matchGrade → boolInjections
- **INFO** `field-maps` (L79): bool debugDrawBounds → boolInjections
- **INFO** `field-maps` (L80): bool debugLogValues → boolInjections
- **INFO** `field-maps` (L81): float fixedHeightOffset → floatInjections
- **INFO** `field-maps` (L82): bool useFixedHeight → boolInjections
- **INFO** `field-maps` (L85): string excludeLayerName → stringInjections
- **INFO** `field-maps` (L86): bool includeInactiveRenderers → boolInjections
- **INFO** `field-maps` (L90): float maxHeightDeviation → floatInjections
- **INFO** `field-maps` (L92): float maxVertexDistance → floatInjections
- **INFO** `field-maps` (L94): bool debugOutlierFiltering → boolInjections

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Attractions/GapFiller.cs`
*class `GapFiller` : `MonoBehaviour`, 1385 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **BLOCKER** `threading` (L483): Threading — Lua env is single-threaded main-thread only
- **RUNTIME** `no-injection-type` (L212): Vector2[] worldFootprint — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L213): List<Vector2[]> holePolygons — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L214): float[] cornerHeights — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L215): Vector2 center — no injection slot for Vector2; add an injection type or encode as floats/string
- **RUNTIME** `coroutine` (L463): Coroutines — no Lua coroutine bridge wired up; rewrite as update() state machine or add util.cs_generator bridge
- **RUNTIME** `no-injection-type` (L829): Vector3[] vertices — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L830): Vector2[] uv — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L831): int[] triangles — only GameObject[] lists are injectable
- **WARN** `aot-ungenerated`: Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: LayerMask, MeshFilter, Resources — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.
- **WARN** `event-subscription` (L111): Subscribes to event 'OnAnyLevelTemplateChanged' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `event-subscription` (L112): Subscribes to event 'OnAnyPropTemplateChanged' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `unknown-on-method` (L182): OnLevelTemplateChanged() — if this is a Unity message it has no relay; if a plain method, ignore
- **WARN** `field-unknown-type` (L211): FloorHeightSampler heightSampler — unknown type; verify an injection slot exists or pass via script injection
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Application, Debug, Mathf, Time
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions
- **INFO** `field-maps` (L67): float verticesPerMeter → floatInjections
- **INFO** `field-maps` (L70): float boundsPadding → floatInjections
- **INFO** `field-maps` (L73): float edgeBlendDistance → floatInjections
- **INFO** `field-maps` (L76): Material floorMaterial → materialInjections
- **INFO** `field-maps` (L80): bool autoRegenerate → boolInjections
- **INFO** `field-maps` (L83): float regenerateDelay → floatInjections
- **INFO** `field-maps` (L86): bool debugLog → boolInjections
- **INFO** `field-maps` (L87): bool showGizmos → boolInjections
- **INFO** `field-maps` (L204): GameObject runtimeFloor → injections (GameObject)
- **INFO** `field-maps` (L205): string sourceName → stringInjections
- **INFO** `field-maps` (L216): bool cutsHole → boolInjections

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyAnimation.cs`
*class `EasyAnimation` : `EasyEvent`, 154 LOC*

- **BLOCKER** `not-monobehaviour` (L6): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyAnimationEvent.cs`
*class `EasyAnimationEvent` : `EasyEvent`, 267 LOC*

- **BLOCKER** `not-monobehaviour` (L14): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyAudio.cs`
*class `EasyAudio` : `EasyEvent`, 145 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyBend.cs`
*class `EasyBend` : `EasyEvent`, 151 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyBroadcast.cs`
*class `EasyBroadcast` : `EasyEvent`, 26 LOC*

- **BLOCKER** `not-monobehaviour` (L4): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyChase.cs`
*class `EasyChase` : `EasyEvent`, 152 LOC*

- **BLOCKER** `not-monobehaviour` (L7): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyCollision.cs`
*class `EasyCollision` : `EasyEvent`, 96 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyDelay.cs`
*class `EasyDelay` : `EasyEvent`, 32 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyDestroy.cs`
*class `EasyDestroy` : `EasyEvent`, 9 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyDetect.cs`
*class `EasyDetect` : `EasyEvent`, 250 LOC*

- **BLOCKER** `not-monobehaviour` (L91): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyDisableAboveActive.cs`
*class `EasyDisableAboveActive` : `EasyEvent`, 35 LOC*

- **BLOCKER** `not-monobehaviour` (L4): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyEnemy.cs`
*class `EasyEnemy` : `EasyEvent`, 53 LOC*

- **BLOCKER** `not-monobehaviour` (L7): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyForce.cs`
*class `EasyForce` : `EasyEvent`, 51 LOC*

- **BLOCKER** `not-monobehaviour` (L4): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyForceFilter.cs`
*class `EasyForceFilter` : `EasyEvent`, 24 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyInteraction.cs`
*class `EasyInteraction` : `EasyEvent`, 131 LOC*

- **BLOCKER** `not-monobehaviour` (L6): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyLayer.cs`
*class `EasyLayer` : `EasyEvent`, 41 LOC*

- **BLOCKER** `not-monobehaviour` (L23): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyLog.cs`
*class `EasyLog` : `EasyEvent`, 12 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyLookAt.cs`
*class `EasyLookAt` : `EasyEvent`, 70 LOC*

- **BLOCKER** `not-monobehaviour` (L8): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyLoop.cs`
*class `EasyLoop` : `EasyEvent`, 15 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyLua.cs`
*class `EasyLua` : `EasyEvent`, 226 LOC*

- **BLOCKER** `not-monobehaviour` (L22): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyMaterial.cs`
*class `EasyMaterial` : `EasyEvent`, 30 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyMath.cs`
*class `EasyMath` : `EasyEvent`, 188 LOC*

- **BLOCKER** `not-monobehaviour` (L4): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyMove.cs`
*class `EasyMove` : `EasyEvent`, 360 LOC*

- **BLOCKER** `not-monobehaviour` (L6): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyMovementAnimation.cs`
*class `EasyMovementAnimation` : `EasyAnimation`, 56 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyAnimation' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyMusicArea.cs`
*class `EasyMusicArea` : `EasyEvent`, 71 LOC*

- **BLOCKER** `not-monobehaviour` (L4): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyNoise.cs`
*class `EasyNoise` : `EasyEvent`, 64 LOC*

- **BLOCKER** `not-monobehaviour` (L4): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyObject.cs`
*class `EasyObject` : `EasyEvent`, 251 LOC*

- **BLOCKER** `not-monobehaviour` (L10): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyOffScreen.cs`
*class `EasyOffScreen` : `EasyOnScreen`, 17 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyOnScreen' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyOnScreen.cs`
*class `EasyOnScreen` : `EasyEvent`, 74 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyParent.cs`
*class `EasyParent` : `EasyEvent`, 29 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyParticle.cs`
*class `EasyParticle` : `EasyEvent`, 88 LOC*

- **BLOCKER** `not-monobehaviour` (L10): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyPhysics.cs`
*class `EasyPhysics` : `EasyEvent`, 42 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyPicker.cs`
*class `EasyPicker` : `EasyEvent`, 203 LOC*

- **BLOCKER** `not-monobehaviour` (L11): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyPlatformRouter.cs`
*class `EasyPlatformRouter` : `EasyEvent`, 54 LOC*

- **BLOCKER** `not-monobehaviour` (L4): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyReceiver.cs`
*class `EasyReceiver` : `EasyEvent`, 178 LOC*

- **BLOCKER** `not-monobehaviour` (L7): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyRouter.cs`
*class `EasyRouter` : `EasyEvent`, 48 LOC*

- **BLOCKER** `not-monobehaviour` (L2): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyScale.cs`
*class `EasyScale` : `EasyEvent`, 61 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyShake.cs`
*class `EasyShake` : `EasyEvent`, 65 LOC*

- **BLOCKER** `not-monobehaviour` (L4): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyShatter.cs`
*class `EasyShatter` : `EasyEvent`, 77 LOC*

- **BLOCKER** `not-monobehaviour` (L21): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasySpin.cs`
*class `EasySpin` : `EasyEvent`, 310 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyStart.cs`
*class `EasyStart` : `EasyEvent`, 11 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyStop.cs`
*class `EasyStop` : `EasyEvent`, 14 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyTag.cs`
*class `EasyTag` : `EasyEvent`, 33 LOC*

- **BLOCKER** `not-monobehaviour` (L23): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyTarget.cs`
*class `EasyTarget` : `EasyEvent`, 41 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyThrow.cs`
*class `EasyThrow` : `EasyEvent`, 134 LOC*

- **BLOCKER** `not-monobehaviour` (L7): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyUV.cs`
*class `EasyUV` : `EasyEvent`, 86 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyUndetect.cs`
*class `EasyUndetect` : `EasyEvent`, 412 LOC*

- **BLOCKER** `not-monobehaviour` (L85): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyUnparent.cs`
*class `EasyUnparent` : `EasyEvent`, 11 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyVariantPicker.cs`
*class `EasyVariantPicker` : `EasyEvent`, 17 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyVars.cs`
*class `EasyVars` : `EasyEvent`, 5 LOC*

- **BLOCKER** `not-monobehaviour` (L2): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyVelocityTrigger.cs`
*class `EasyVelocityTrigger` : `EasyEvent`, 61 LOC*

- **BLOCKER** `not-monobehaviour` (L3): Base 'EasyEvent' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/EasyEvent/EasyVoice.cs`
*class `EasyVoice` : `EasyEvent`, 250 LOC*

- **BLOCKER** `not-monobehaviour` (L5): Base 'EasyEvent' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Entity/Entity.cs`
*class `Entity` : `—`, 178 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Entity/StandardEntity.cs`
*class `StandardEntity` : `—`, 669 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Lua/Editor/EasyLuaEditor.cs`
*class `EasyLuaEditor` : `—`, 62 LOC*

- **BLOCKER** `editor-code`: Editor script / uses UnityEditor — not a runtime transpile target
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Lua/Editor/LuaBehaviourEditor.cs`
*class `LuaBehaviourEditor` : `Editor`, 51 LOC*

- **BLOCKER** `editor-code`: Editor script / uses UnityEditor — not a runtime transpile target

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Lua/Editor/LuaInjectionEditorGUI.cs`
*class `LuaInjectionEditorGUI` : `—`, 727 LOC*

- **BLOCKER** `editor-code`: Editor script / uses UnityEditor — not a runtime transpile target

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Lua/ILuaInjectable.cs`
*class `ILuaInjectable` : `—`, 25 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Lua/LuaBehaviour.cs`
*class `LuaBehaviour` : `MonoBehaviour`, 439 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **RUNTIME** `no-injection-type` (L101): Injection[] injections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L102): FloatInjection[] floatInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L103): StringInjection[] stringInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L104): BoolInjection[] boolInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L105): IntInjection[] intInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L106): ScriptInjection[] scriptInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L107): AudioClipInjection[] audioClipInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L108): Vector3Injection[] vector3Injections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L109): ColorInjection[] colorInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L110): TransformInjection[] transformInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L111): MaterialInjection[] materialInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L112): SpriteInjection[] spriteInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L113): TextureInjection[] textureInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L114): ComponentInjection[] componentInjections — only GameObject[] lists are injectable
- **RUNTIME** `no-injection-type` (L115): GameObjectListInjection[] gameObjectListInjections — only GameObject[] lists are injectable
- **WARN** `field-unknown-type` (L100): TextAsset luaScript — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `field-unknown-type` (L245): LuaTable ScriptScope — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `event-subscription` (L372): Subscribes to event 'OnNetEvent' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Time
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions
- **INFO** `field-maps` (L9): string name → stringInjections
- **INFO** `field-maps` (L10): GameObject value → injections (GameObject)
- **INFO** `field-maps` (L15): string name → stringInjections
- **INFO** `field-maps` (L16): float value → floatInjections
- **INFO** `field-maps` (L21): string name → stringInjections
- **INFO** `field-maps` (L22): string value → stringInjections
- **INFO** `field-maps` (L27): string name → stringInjections
- **INFO** `field-maps` (L28): bool value → boolInjections
- **INFO** `field-maps` (L33): string name → stringInjections
- **INFO** `field-maps` (L34): int value → intInjections
- **INFO** `field-maps` (L39): string name → stringInjections
- **INFO** `field-maps` (L40): LuaBehaviour value → componentInjections
- **INFO** `field-maps` (L45): string name → stringInjections
- **INFO** `field-maps` (L46): AudioClip value → audioClipInjections
- **INFO** `field-maps` (L51): string name → stringInjections
- **INFO** `field-maps` (L52): Vector3 value → vector3Injections
- **INFO** `field-maps` (L57): string name → stringInjections
- **INFO** `field-maps` (L58): Color value → colorInjections
- **INFO** `field-maps` (L63): string name → stringInjections
- **INFO** `field-maps` (L64): Transform value → transformInjections
- **INFO** `field-maps` (L69): string name → stringInjections
- **INFO** `field-maps` (L70): Material value → materialInjections
- **INFO** `field-maps` (L75): string name → stringInjections
- **INFO** `field-maps` (L76): Sprite value → spriteInjections
- **INFO** `field-maps` (L81): string name → stringInjections
- **INFO** `field-maps` (L82): Texture value → textureInjections
- **INFO** `field-maps` (L87): string name → stringInjections
- **INFO** `field-maps` (L88): Component value → componentInjections
- **INFO** `field-maps` (L93): string name → stringInjections
- **INFO** `field-maps` (L94): GameObject[] value → gameObjectListInjections

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Net/BeaconBroadcaster.cs`
*class `BeaconBroadcaster` : `IDisposable`, 131 LOC*

- **BLOCKER** `not-monobehaviour` (L22): Base 'IDisposable' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Net/DiscoveryListener.cs`
*class `DiscoveryListener` : `IDisposable`, 170 LOC*

- **BLOCKER** `not-monobehaviour` (L13): Base 'IDisposable' is not a MonoBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Net/NetLog.cs`
*class `NetLog` : `—`, 26 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Net/NetPlatform.cs`
*class `NetPlatform` : `—`, 157 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Net/NetRegistry.cs`
*class `NetRegistry` : `—`, 82 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Net/NetSessionArbiter.cs`
*class `NetSessionArbiter` : `MonoBehaviour`, 619 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **BLOCKER** `threading` (L210): Threading — Lua env is single-threaded main-thread only
- **WARN** `field-unknown-type` (L134): DiscoveryListener.BeaconInfo info — unknown type; verify an injection slot exists or pass via script injection
- **WARN** `event-subscription` (L172): Subscribes to event 'OnGlobalEvent' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `event-subscription` (L173): Subscribes to event 'OnSessionPaired' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **WARN** `unknown-on-method` (L192): OnSessionPaired() — if this is a Unity message it has no relay; if a plain method, ignore
- **WARN** `unknown-on-method` (L449): OnGlobalEvent() — if this is a Unity message it has no relay; if a plain method, ignore
- **WARN** `event-subscription` (L498): Subscribes to event 'OnBeacon' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug, Mathf, Time
- **INFO** `require-component`: [RequireComponent] — LuaBehaviour won't auto-add; converter must add components to the prefab explicitly
- **INFO** `field-maps` (L34): string parkId → stringInjections
- **INFO** `field-maps` (L37): bool allowHosting → boolInjections
- **INFO** `field-maps` (L40): float listenBaseSeconds → floatInjections
- **INFO** `field-maps` (L41): float beaconStaleSeconds → floatInjections
- **INFO** `field-maps` (L42): float reelectionStepSeconds → floatInjections
- **INFO** `field-maps` (L43): float joinTimeoutSeconds → floatInjections
- **INFO** `field-maps` (L44): float kioskGraceSeconds → floatInjections
- **INFO** `field-maps` (L49): bool IsHost → boolInjections
- **INFO** `field-maps` (L50): int HostedPeerCount → intInjections
- **INFO** `field-maps` (L97): bool CanHost → boolInjections
- **INFO** `field-maps` (L110): string channelOverride → stringInjections
- **INFO** `field-maps` (L117): string Channel → stringInjections
- **INFO** `field-maps` (L134): float seenAt → floatInjections

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Net/PeerRelayServer.cs`
*class `PeerRelayServer` : `—`, 226 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Net/SessionContext.cs`
*class `SessionContext` : `—`, 85 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Features/Player/PlayerRig.cs`
*class `PlayerRig` : `MonoBehaviour`, 101 LOC*

- **BLOCKER** `core-conditional`: Contains #if DREAMPARKCORE — SDK-synced core file, not creator content
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
- **INFO** `aot-static-utility`: Static utility types via reflection (usually fine): Debug
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions
- **INFO** `field-maps` (L10): string gameId → stringInjections

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Utils/CoreExtensions.cs`
*class `CoreExtensions` : `—`, 323 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Utils/Extensions.cs`
*class `Extensions` : `—`, 1325 LOC*

- **BLOCKER** `not-monobehaviour`: No base type (static/utility class) — nothing to host on a LuaBehaviour

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Utils/MainThreadDispatcher.cs`
*class `MainThreadDispatcher` : `MonoBehaviour`, 55 LOC*

- **BLOCKER** `threading` (L3): Threading — Lua env is single-threaded main-thread only
- **INFO** `static-state`: Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Utils/ReadyOnlyAtrribute.cs`
*class `ReadOnlyAttribute` : `PropertyAttribute`, 25 LOC*

- **BLOCKER** `not-monobehaviour` (L6): Base 'PropertyAttribute' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them

## 🔴 KEEP_CSHARP — `Assets/DreamPark/Scripts/Utils/ShowIf.cs`
*class `ShowIfAttribute` : `PropertyAttribute`, 104 LOC*

- **BLOCKER** `not-monobehaviour` (L11): Base 'PropertyAttribute' is not a MonoBehaviour
- **INFO** `editor-region-dropped`: #if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them
