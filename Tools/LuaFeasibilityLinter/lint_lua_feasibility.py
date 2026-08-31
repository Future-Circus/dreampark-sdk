#!/usr/bin/env python3
"""
DreamPark C# → XLua feasibility linter (PROTOTYPE).

Classifies MonoBehaviour scripts by how cleanly they could be auto-translated
to a LuaBehaviour .lua.txt script, given the CURRENT SDK Lua surface:

  CLEAN          — maps 1:1 onto the existing LuaBehaviour surface
  CLEAN_WARN     — translatable, but has AOT / pattern warnings to resolve
  NEEDS_RUNTIME  — blocked on SDK runtime additions (relays, delegate sigs,
                   injection types, coroutine bridge)
  KEEP_CSHARP    — not a sensible transpile target (SDK internals, editor
                   code, threading/async, non-MonoBehaviour, ...)

Ground truth for the whitelists (keep in sync):
  Assets/DreamPark/Scripts/Features/Lua/LuaBehaviour.cs        (direct messages, injections)
  Assets/DreamPark/Scripts/Features/Lua/LuaMessageRelays.cs    (opt-in relayed messages)
  Assets/DreamPark/Editor/DreamParkLuaConfig.cs                (AOT-generated types, delegate sigs)

This is a regex/heuristic prototype — the production version should be a
Roslyn analyzer inside the editor. Known blind spots are listed in REPORT.md.

Usage:
  python3 lint_lua_feasibility.py <file-or-dir> [...] [--json out.json] [--md out.md]
"""

import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

# ── Whitelists derived from the SDK source ──────────────────────────────────

# LuaBehaviour direct wiring + LuaMessageRelays opt-in relays.
SUPPORTED_MESSAGES = {
    "Awake", "Start", "Update", "OnDestroy", "OnEnable", "OnDisable",
    "FixedUpdate", "LateUpdate",
    "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay",
    "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay",
    "OnApplicationPause", "OnApplicationFocus",
}

# Editor-only messages: dropped silently with an info (they never run on device).
EDITOR_ONLY_MESSAGES = {"OnValidate", "OnDrawGizmos", "OnDrawGizmosSelected", "Reset"}

# All other Unity magic methods we recognise → NEEDS_RUNTIME (no relay exists).
KNOWN_UNSUPPORTED_MESSAGES = {
    "OnMouseDown", "OnMouseUp", "OnMouseUpAsButton", "OnMouseEnter", "OnMouseExit",
    "OnMouseOver", "OnMouseDrag",
    "OnBecameVisible", "OnBecameInvisible", "OnWillRenderObject",
    "OnPreRender", "OnPostRender", "OnRenderObject", "OnRenderImage",
    "OnAnimatorIK", "OnAnimatorMove",
    "OnParticleCollision", "OnParticleTrigger", "OnParticleSystemStopped",
    "OnJointBreak", "OnControllerColliderHit",
    "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
    "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
    "OnApplicationQuit", "OnAudioFilterRead", "OnTransformChildrenChanged",
    "OnTransformParentChanged", "OnRectTransformDimensionsChange",
    "OnBeforeTransformParentChanged", "OnCanvasGroupChanged", "OnLevelWasLoaded",
}

# DreamParkLuaConfig.LuaCallCSharp — AOT-safe generated wrappers.
AOT_GENERATED_TYPES = {
    "Vector3", "Vector2", "Quaternion", "Color", "Transform", "GameObject",
    "Component", "Material", "Sprite", "Texture", "Texture2D", "Renderer",
    "AudioSource", "Rigidbody", "Collider", "Collision",
}

# Common UnityEngine types creators reach for that are NOT generated → AOT warning.
AOT_WATCHLIST = {
    "Animator", "Animation", "ParticleSystem", "Rigidbody2D", "Collider2D",
    "AudioClip", "Light", "Camera", "CharacterController", "LineRenderer",
    "TrailRenderer", "MeshRenderer", "SkinnedMeshRenderer", "MeshFilter",
    "BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider",
    "CanvasGroup", "RectTransform", "Canvas", "TextMesh", "TextMeshPro",
    "TextMeshProUGUI", "Image", "Text", "Button", "Slider", "Toggle",
    "NavMeshAgent", "WheelCollider", "Cloth", "Terrain", "VideoPlayer",
    "SpriteRenderer", "Gradient", "AnimationCurve", "LayerMask", "Physics",
    "Mathf", "Time", "Random", "Input", "Screen", "Application", "Resources",
}
# Of the watchlist, these are pure static/utility surfaces that are cheap via
# reflection and rarely stripped (still worth listing, lower severity).
AOT_LOW_RISK = {"Mathf", "Time", "Random", "Debug", "Screen", "Application", "Physics", "Input"}

# Field type → LuaBehaviour injection slot (LuaBehaviour.cs Injection classes).
INJECTABLE_TYPES = {
    "float": "floatInjections", "double": "floatInjections",
    "int": "intInjections", "long": "intInjections",
    "string": "stringInjections", "bool": "boolInjections",
    "GameObject": "injections (GameObject)",
    "Transform": "transformInjections",
    "Material": "materialInjections", "Sprite": "spriteInjections",
    "Texture": "textureInjections", "Texture2D": "textureInjections",
    "Color": "colorInjections", "Vector3": "vector3Injections",
    "AudioClip": "audioClipInjections",
    "GameObject[]": "gameObjectListInjections",
}
# Any UnityEngine.Component subclass can ride componentInjections.
COMPONENT_SUBCLASSES = {
    "Renderer", "MeshRenderer", "SkinnedMeshRenderer", "SpriteRenderer",
    "AudioSource", "Rigidbody", "Rigidbody2D", "Collider", "BoxCollider",
    "SphereCollider", "CapsuleCollider", "MeshCollider", "Collider2D",
    "Animator", "Animation", "ParticleSystem", "Light", "Camera",
    "CharacterController", "LineRenderer", "TrailRenderer", "MeshFilter",
    "CanvasGroup", "RectTransform", "TextMesh", "TextMeshProUGUI", "Image",
    "Text", "Button", "Slider", "Toggle", "Component", "Behaviour",
    "MonoBehaviour", "LuaBehaviour",
}

# CSharpCallLua delegate signatures that exist today.
SUPPORTED_DELEGATE_SIGS = {"Action", "Action<bool>", "Action<string>",
                           "Action<Collider>", "Action<Collision>"}

CS_KEYWORD_NOT_TYPE = {
    "if", "for", "foreach", "while", "switch", "return", "new", "using",
    "public", "private", "protected", "internal", "static", "void", "var",
    "class", "struct", "enum", "namespace", "typeof", "nameof", "lock",
    "catch", "get", "set", "else", "do", "try", "base", "this", "out", "ref",
    "in", "is", "as", "readonly", "const", "override", "virtual", "abstract",
    "sealed", "partial", "params", "default", "true", "false", "null",
}


@dataclass
class Finding:
    severity: str   # BLOCKER | RUNTIME | WARN | INFO
    rule: str
    detail: str
    line: int = 0


@dataclass
class Result:
    path: str
    classification: str = "CLEAN"
    class_name: str = ""
    base_type: str = ""
    findings: list = field(default_factory=list)
    loc: int = 0

    def add(self, severity, rule, detail, line=0):
        self.findings.append(Finding(severity, rule, detail, line))


# ── Source preprocessing ─────────────────────────────────────────────────────

def strip_comments_and_strings(src: str) -> str:
    """Blank out comments and string literals, preserving line numbers."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        two = src[i:i + 2]
        if two == "//":
            j = src.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i)); i = j
        elif two == "/*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("".join(ch if ch == "\n" else " " for ch in src[i:j])); i = j
        elif c == '"':
            # verbatim string?
            verbatim = i > 0 and src[i - 1] == "@"
            j = i + 1
            while j < n:
                if src[j] == '"' and (not verbatim) and src[j - 1] != "\\":
                    break
                if src[j] == '"' and verbatim:
                    if src[j:j + 2] == '""':
                        j += 2; continue
                    break
                j += 1
            j = min(j + 1, n)
            out.append('"' + " " * max(0, j - i - 2) + '"'); i = j
        elif c == "'":
            j = src.find("'", i + 1)
            while j > 0 and src[j - 1] == "\\":
                j = src.find("'", j + 1)
            j = n if j < 0 else j + 1
            out.append(" " * (j - i)); i = j
        else:
            out.append(c); i += 1
    return "".join(out)


def line_of(src: str, pos: int) -> int:
    return src.count("\n", 0, pos) + 1


def strip_editor_regions(src: str):
    """Blank out `#if UNITY_EDITOR … #endif` regions (they never ship on device),
    preserving line numbers. Returns (stripped_src, stripped_any). Handles nesting."""
    lines = src.split("\n")
    out, depth, stripped = [], 0, False
    for ln in lines:
        s = ln.strip()
        if depth > 0:
            if s.startswith("#if"):
                depth += 1
            elif s.startswith("#endif"):
                depth -= 1
            elif s.startswith("#else") and depth == 1:
                depth = 0  # runtime branch of `#if UNITY_EDITOR / #else` — keep it
            out.append("")
            stripped = True
            continue
        if re.match(r"#if\s+UNITY_EDITOR\b", s):
            depth = 1
            out.append("")
            stripped = True
            continue
        out.append(ln)
    return "\n".join(out), stripped


# ── Analysis ─────────────────────────────────────────────────────────────────

def analyze(path: Path) -> Result:
    raw = path.read_text(encoding="utf-8", errors="replace")
    src = strip_comments_and_strings(raw)
    src, had_editor_regions = strip_editor_regions(src)
    r = Result(path=str(path), loc=raw.count("\n") + 1)
    if had_editor_regions:
        r.add("INFO", "editor-region-dropped",
              "#if UNITY_EDITOR region(s) ignored — never ship on device; converter drops them")

    # class declaration & base type
    m = re.search(r"\b(?:public|internal)?\s*(?:sealed\s+|abstract\s+|partial\s+)*class\s+(\w+)\s*:\s*([\w.<>]+)", src)
    if m:
        r.class_name, r.base_type = m.group(1), m.group(2)
    else:
        m2 = re.search(r"\bclass\s+(\w+)", src)
        r.class_name = m2.group(1) if m2 else path.stem

    is_editor = "/Editor/" in str(path) or re.search(r"\busing\s+UnityEditor\b", src)
    if is_editor:
        r.classification = "KEEP_CSHARP"
        r.add("BLOCKER", "editor-code", "Editor script / uses UnityEditor — not a runtime transpile target")
        return r

    if not r.base_type:
        r.classification = "KEEP_CSHARP"
        r.add("BLOCKER", "not-monobehaviour", "No base type (static/utility class) — nothing to host on a LuaBehaviour")
        return r

    if r.base_type == "ScriptableObject":
        r.classification = "KEEP_CSHARP"
        r.add("BLOCKER", "scriptable-object", "ScriptableObject — no Lua equivalent; keep as C# data")
        return r

    if r.base_type not in ("MonoBehaviour", "NetworkBehaviour") and r.base_type not in COMPONENT_SUBCLASSES:
        # Inherits from an SDK class (LevelTemplate, LuaVoidRelay, ...) or plain object.
        if re.search(r"\b(LevelTemplate|PropTemplate|LuaBehaviour|Template)\b", r.base_type):
            r.add("BLOCKER", "sdk-base-class", f"Inherits SDK type '{r.base_type}' — SDK component, not creator gameplay code")
            r.classification = "KEEP_CSHARP"
            return r
        if not re.search(r"MonoBehaviour", src):
            r.add("BLOCKER", "not-monobehaviour", f"Base '{r.base_type}' is not a MonoBehaviour", line_of(src, m.start()) if m else 0)
            r.classification = "KEEP_CSHARP"
            return r

    blockers = runtime = warns = 0

    def bump(sev):
        nonlocal blockers, runtime, warns
        if sev == "BLOCKER": blockers += 1
        elif sev == "RUNTIME": runtime += 1
        elif sev == "WARN": warns += 1

    # ── Hard blockers ──
    for pat, rule, msg in [
        (r"\basync\s+\w|\bawait\s", "async-await", "async/await — no Lua bridge; keep in C# or redesign"),
        (r"\bSystem\.Threading\b|\bnew\s+Thread\b|\bTask\.Run\b", "threading", "Threading — Lua env is single-threaded main-thread only"),
        (r"\bunsafe\b", "unsafe", "unsafe code"),
        (r"\bDllImport\b", "native-interop", "P/Invoke native interop"),
        (r"\[\s*ExecuteInEditMode\s*\]|\[\s*ExecuteAlways\s*\]", "execute-in-editmode", "ExecuteInEditMode/ExecuteAlways — LuaBehaviour only runs in play mode"),
    ]:
        mm = re.search(pat, src)
        if mm:
            r.add("BLOCKER", rule, msg, line_of(src, mm.start())); bump("BLOCKER")

    # DREAMPARKCORE / SDK-internal signals
    if re.search(r"#if\s+DREAMPARKCORE", src):
        r.add("BLOCKER", "core-conditional", "Contains #if DREAMPARKCORE — SDK-synced core file, not creator content")
        bump("BLOCKER")

    # ── Unity magic methods ──
    declared_msgs = set()
    for mm in re.finditer(r"\b(?:void|IEnumerator)\s+(On\w+|Awake|Start|Update|FixedUpdate|LateUpdate|Reset)\s*\(", src):
        declared_msgs.add((mm.group(1), line_of(src, mm.start())))
    for name, ln in sorted(declared_msgs):
        if name in SUPPORTED_MESSAGES:
            continue
        if name in EDITOR_ONLY_MESSAGES:
            r.add("INFO", "editor-only-message", f"{name}() is editor-only — dropped in translation", ln)
        elif name in KNOWN_UNSUPPORTED_MESSAGES:
            r.add("RUNTIME", "unsupported-message", f"{name}() has no Lua relay — needs a new relay class + delegate sig in DreamParkLuaConfig", ln)
            bump("RUNTIME")
        elif name.startswith("On"):
            r.add("WARN", "unknown-on-method", f"{name}() — if this is a Unity message it has no relay; if a plain method, ignore", ln)
            bump("WARN")

    # ── Coroutines ──
    if re.search(r"\bStartCoroutine\b|\byield\s+return\b|\bIEnumerator\b", src):
        mm = re.search(r"\bStartCoroutine\b|\byield\s+return\b|\bIEnumerator\b", src)
        r.add("RUNTIME", "coroutine", "Coroutines — no Lua coroutine bridge wired up; rewrite as update() state machine or add util.cs_generator bridge", line_of(src, mm.start()))
        bump("RUNTIME")

    # ── Invoke / InvokeRepeating (translatable pattern) ──
    mm = re.search(r"\bInvokeRepeating\s*\(|\bInvoke\s*\(\s*\"", src)
    if mm:
        r.add("WARN", "invoke-timer", "Invoke/InvokeRepeating — translate to timer accumulation in update()", line_of(src, mm.start()))
        bump("WARN")

    # ── Event subscriptions / delegate signatures ──
    for mm in re.finditer(r"\.\s*(\w+)\s*\+=", src):
        r.add("WARN", "event-subscription",
              f"Subscribes to event '{mm.group(1)}' — Lua can only receive Action/Action<bool|string|Collider|Collision>; other signatures need CSharpCallLua additions",
              line_of(src, mm.start()))
        bump("WARN")
    for mm in re.finditer(r"\bAddListener\s*\(", src):
        r.add("WARN", "unityevent-listener", "UnityEvent.AddListener — verify the event's arg types map to a supported delegate signature", line_of(src, mm.start()))
        bump("WARN")

    # ── Serialized fields → injection mapping ──
    body = src
    for mm in re.finditer(
            r"(?:\[SerializeField\]\s*(?:private|protected)?|public)\s+"
            r"(?!class|enum|struct|interface|delegate|static|const|override|event)"
            r"([\w.<>\[\]]+)\s+(\w+)\s*(?:=[^;]*)?;", body):
        ftype, fname = mm.group(1), mm.group(2)
        ln = line_of(src, mm.start())
        if ftype in CS_KEYWORD_NOT_TYPE or ftype in ("void",):
            continue
        base = ftype.replace("UnityEngine.", "")
        if base in INJECTABLE_TYPES:
            r.add("INFO", "field-maps", f"{ftype} {fname} → {INJECTABLE_TYPES[base]}", ln)
        elif base in COMPONENT_SUBCLASSES:
            r.add("INFO", "field-maps", f"{ftype} {fname} → componentInjections", ln)
        elif base in ("Vector2", "Quaternion", "LayerMask", "AnimationCurve", "Gradient", "Rect", "Bounds"):
            r.add("RUNTIME", "no-injection-type", f"{ftype} {fname} — no injection slot for {base}; add an injection type or encode as floats/string", ln)
            bump("RUNTIME")
        elif re.match(r".*\[\]$|^List<", base):
            r.add("RUNTIME", "no-injection-type", f"{ftype} {fname} — only GameObject[] lists are injectable", ln)
            bump("RUNTIME")
        else:
            # enum defined in-file?
            if re.search(rf"\benum\s+{re.escape(base)}\b", src):
                r.add("RUNTIME", "no-injection-type", f"enum {base} {fname} — no enum injection; use intInjections + named constants", ln)
                bump("RUNTIME")
            else:
                r.add("WARN", "field-unknown-type", f"{ftype} {fname} — unknown type; verify an injection slot exists or pass via script injection", ln)
                bump("WARN")

    # ── AOT / IL2CPP surface ──
    used_types = set()
    for mm in re.finditer(r"\bGetComponent(?:s|InChildren|InParent)?\s*<\s*([\w.]+)\s*>", src):
        used_types.add(mm.group(1).split(".")[-1])
    for mm in re.finditer(r"\b([A-Z]\w+)\s*\.\s*[A-Za-z_]", src):
        used_types.add(mm.group(1))
    for mm in re.finditer(r"\bnew\s+([A-Z]\w+)\s*\(", src):
        used_types.add(mm.group(1))
    aot_hits = sorted((used_types & AOT_WATCHLIST) - AOT_GENERATED_TYPES - AOT_LOW_RISK)
    aot_low = sorted(used_types & AOT_LOW_RISK)
    if aot_hits:
        r.add("WARN", "aot-ungenerated",
              f"Types used but NOT in DreamParkLuaConfig.LuaCallCSharp: {', '.join(aot_hits)} — works in Editor (reflection), risks AOT stripping on Quest/iOS. Add to config + XLua ▸ Generate Code.")
        bump("WARN")
    if aot_low:
        r.add("INFO", "aot-static-utility", f"Static utility types via reflection (usually fine): {', '.join(aot_low)}")

    # ── Misc info ──
    if re.search(r"\[\s*RequireComponent", src):
        r.add("INFO", "require-component", "[RequireComponent] — LuaBehaviour won't auto-add; converter must add components to the prefab explicitly")
    if re.search(r"\bstatic\s+(?!void\s+Main)\w+[\w.<>\[\]]*\s+\w+\s*(=|;)", src):
        r.add("INFO", "static-state", "Static fields — Lua scripts share one global env; emulate via a registered global table, beware name collisions")
    if re.search(r"\bInput\s*\.", src):
        r.add("WARN", "input-api", "Uses UnityEngine.Input — DreamPark forbids keyboard/desktop input as primary interaction; redesign for hands/physical play")
        bump("WARN")

    # ── Classification ──
    if blockers:
        r.classification = "KEEP_CSHARP"
    elif runtime:
        r.classification = "NEEDS_RUNTIME"
    elif warns:
        r.classification = "CLEAN_WARN"
    else:
        r.classification = "CLEAN"
    return r


# ── Reporting ────────────────────────────────────────────────────────────────

SEV_ORDER = {"BLOCKER": 0, "RUNTIME": 1, "WARN": 2, "INFO": 3}
BADGE = {"CLEAN": "✅ CLEAN", "CLEAN_WARN": "🟡 CLEAN_WARN",
         "NEEDS_RUNTIME": "🟠 NEEDS_RUNTIME", "KEEP_CSHARP": "🔴 KEEP_CSHARP"}


def to_markdown(results, root: Path) -> str:
    counts = {}
    for res in results:
        counts[res.classification] = counts.get(res.classification, 0) + 1
    lines = ["# C# → XLua Feasibility Report", ""]
    lines.append(f"Scanned **{len(results)}** scripts.  " +
                 "  ".join(f"{BADGE[k]}: **{v}**" for k, v in sorted(counts.items(), key=lambda kv: kv[0])))
    lines.append("")
    order = {"CLEAN": 0, "CLEAN_WARN": 1, "NEEDS_RUNTIME": 2, "KEEP_CSHARP": 3}
    for res in sorted(results, key=lambda x: (order[x.classification], x.path)):
        rel = str(Path(res.path))
        try:
            rel = str(Path(res.path).relative_to(root))
        except ValueError:
            pass
        lines.append(f"## {BADGE[res.classification]} — `{rel}`")
        lines.append(f"*class `{res.class_name}` : `{res.base_type or '—'}`, {res.loc} LOC*")
        lines.append("")
        for f in sorted(res.findings, key=lambda f: (SEV_ORDER[f.severity], f.line)):
            loc = f" (L{f.line})" if f.line else ""
            lines.append(f"- **{f.severity}** `{f.rule}`{loc}: {f.detail}")
        if not res.findings:
            lines.append("- No findings — direct translation candidate.")
        lines.append("")
    return "\n".join(lines)


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    md_out = json_out = None
    if "--md" in argv:
        md_out = argv[argv.index("--md") + 1]
    if "--json" in argv:
        json_out = argv[argv.index("--json") + 1]
    files = []
    for a in args:
        p = Path(a)
        if p.is_dir():
            files += sorted(p.rglob("*.cs"))
        elif p.suffix == ".cs":
            files.append(p)
    if not files:
        print(__doc__)
        return 1
    root = Path(args[0]).resolve() if Path(args[0]).is_dir() else Path.cwd()
    results = [analyze(f) for f in files]

    for res in sorted(results, key=lambda x: x.classification):
        print(f"{BADGE[res.classification]:<22} {res.class_name:<28} {res.path}")
    counts = {}
    for res in results:
        counts[res.classification] = counts.get(res.classification, 0) + 1
    print("\nSummary:", "  ".join(f"{k}={v}" for k, v in sorted(counts.items())))

    if md_out:
        Path(md_out).write_text(to_markdown(results, root), encoding="utf-8")
        print(f"markdown → {md_out}")
    if json_out:
        payload = [{"path": r.path, "class": r.class_name, "base": r.base_type,
                    "classification": r.classification, "loc": r.loc,
                    "findings": [vars(f) for f in r.findings]} for r in results]
        Path(json_out).write_text(json.dumps(payload, indent=2), encoding="utf-8")
        print(f"json → {json_out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
