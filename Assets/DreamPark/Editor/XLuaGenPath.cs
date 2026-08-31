#if UNITY_EDITOR
using UnityEngine;

namespace CSObjectWrapEditor
{
    // Redirect XLua's generated C# wrappers into the synced SDK bundle so they
    // ride the dreampark-sdk → dreampark-core sync alongside the rest of
    // Assets/DreamPark/. Without this, XLua drops a top-level Assets/XLua/Gen/
    // folder that is outside the sync rule.
    //
    // GeneratorConfig walks every public static class for a string field
    // attributed [GenPath] and uses it to override common_path.
    public static class DreamParkXLuaGenPath
    {
        [GenPath]
        public static string Path = Application.dataPath + "/DreamPark/ThirdParty/XLua/Gen/";
    }
}
#endif
