using System;
using System.Collections.Generic;
using UnityEngine;
using XLua;

/// <summary>
/// XLua code-generation config for the DreamPark Lua surface.
///
/// The project relies on per-class [LuaCallCSharp] attributes rather than the
/// (commented-out) auto-scan in ExampleConfig. That covers LuaBehaviour / EasyLua /
/// ProfileAPI, but the Unity types that creator scripts now receive as injected
/// variables (Vector3, Color, Transform, Material, …) and the delegate signatures
/// the relays bind (Action&lt;bool&gt;) are NOT auto-generated.
///
/// Without generated wrappers these still work in the Editor (Mono) via reflection,
/// but on IL2CPP/AOT targets — Quest 3S and iOS — member access on un-generated
/// types can hit AOT stripping or allocate. Listing them here makes
/// "XLua ▸ Generate Code" emit fast, AOT-safe wrappers.
///
/// NOTE: this only takes effect after regenerating. Run XLua ▸ Generate Code (then
/// rebuild) whenever this list changes. Generated output lands in
/// Assets/DreamPark/ThirdParty/XLua/Gen/ (see DreamParkXLuaGenPath).
/// </summary>
public static class DreamParkLuaConfig {

    // Types creator Lua now reads/manipulates directly (injected vars + their members).
    [LuaCallCSharp]
    public static List<Type> LuaCallCSharp = new List<Type>() {
        typeof(Vector3),
        typeof(Vector2),
        typeof(Quaternion),
        typeof(Color),
        typeof(Transform),
        typeof(GameObject),
        typeof(Component),
        typeof(Material),
        typeof(Sprite),
        typeof(Texture),
        typeof(Texture2D),
        typeof(Renderer),
        typeof(AudioSource),
        typeof(Rigidbody),
        typeof(Collider),
        typeof(Collision),
    };

    // Delegate signatures bridged from Lua functions into C# (LuaBehaviour / EasyLua
    // callbacks + relay handlers). The enter/exit/stay physics handlers reuse
    // Action<Collider>/Action<Collision>; app pause/focus adds Action<bool>.
    [CSharpCallLua]
    public static List<Type> CSharpCallLua = new List<Type>() {
        typeof(Action),
        typeof(Action<bool>),
        typeof(Action<string>),
        typeof(Action<Collider>),
        typeof(Action<Collision>),
    };

    // Value types worth GC-optimizing so pushing them to Lua doesn't allocate.
    [GCOptimize]
    public static List<Type> GCOptimize = new List<Type>() {
        typeof(Vector3),
        typeof(Vector2),
        typeof(Quaternion),
        typeof(Color),
    };
}
