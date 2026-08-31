using UnityEngine;

/// <summary>
/// Shared interface for any component that supports Lua variable injection.
/// Implemented by LuaBehaviour and EasyLua so the editor GUI can be shared.
/// </summary>
public interface ILuaInjectable {
    TextAsset         luaScript         { get; set; }
    Injection[]       injections        { get; set; }
    FloatInjection[]  floatInjections   { get; set; }
    StringInjection[] stringInjections  { get; set; }
    BoolInjection[]   boolInjections    { get; set; }
    IntInjection[]    intInjections     { get; set; }
    ScriptInjection[]    scriptInjections    { get; set; }
    AudioClipInjection[] audioClipInjections { get; set; }
    Vector3Injection[]        vector3Injections        { get; set; }
    ColorInjection[]          colorInjections          { get; set; }
    TransformInjection[]      transformInjections      { get; set; }
    MaterialInjection[]       materialInjections       { get; set; }
    SpriteInjection[]         spriteInjections         { get; set; }
    TextureInjection[]        textureInjections        { get; set; }
    ComponentInjection[]      componentInjections      { get; set; }
    GameObjectListInjection[] gameObjectListInjections { get; set; }
}
