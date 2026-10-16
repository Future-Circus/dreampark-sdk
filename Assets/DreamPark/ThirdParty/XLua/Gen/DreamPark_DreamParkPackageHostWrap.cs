#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

using XLua;
using System.Collections.Generic;


namespace XLua.CSObjectWrap
{
    using Utils = XLua.Utils;
    public class DreamParkDreamParkPackageHostWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.DreamParkPackageHost);
			Utils.BeginObjectRegister(type, L, translator, 0, 13, 17, 13);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GroupLevelCount", _m_GroupLevelCount);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ResolveGroupSlot", _m_ResolveGroupSlot);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ResolveGroupId", _m_ResolveGroupId);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "LoadLevel", _m_LoadLevel);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "PreloadLevel", _m_PreloadLevel);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "IsLevelReady", _m_IsLevelReady);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ShowOverlay", _m_ShowOverlay);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetOverlayAutoVisibility", _m_SetOverlayAutoVisibility);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetRoomFootprintMeters", _m_SetRoomFootprintMeters);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ActivatePreparedLevel", _m_ActivatePreparedLevel);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "LevelActivated", _e_LevelActivated);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "LevelPresented", _e_LevelPresented);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "LevelChangeFailed", _e_LevelChangeFailed);
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "CurrentLevelIndex", _g_get_CurrentLevelIndex);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "IsChangingLevel", _g_get_IsChangingLevel);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "LastLevelError", _g_get_LastLevelError);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "LevelCount", _g_get_LevelCount);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "kind", _g_get_kind);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "contentId", _g_get_contentId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "container", _g_get_container);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "levelParent", _g_get_levelParent);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "startLevel", _g_get_startLevel);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gameOverLevel", _g_get_gameOverLevel);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "overlayLevel", _g_get_overlayLevel);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "transitionEffect", _g_get_transitionEffect);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "levelLoader", _g_get_levelLoader);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "transitionDelaySeconds", _g_get_transitionDelaySeconds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "overlayAutoVisibility", _g_get_overlayAutoVisibility);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "adventureLevelCount", _g_get_adventureLevelCount);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "adventureLevelNames", _g_get_adventureLevelNames);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "kind", _s_set_kind);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "contentId", _s_set_contentId);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "container", _s_set_container);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "levelParent", _s_set_levelParent);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "startLevel", _s_set_startLevel);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "gameOverLevel", _s_set_gameOverLevel);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "overlayLevel", _s_set_overlayLevel);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "transitionEffect", _s_set_transitionEffect);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "levelLoader", _s_set_levelLoader);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "transitionDelaySeconds", _s_set_transitionDelaySeconds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "overlayAutoVisibility", _s_set_overlayAutoVisibility);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "adventureLevelCount", _s_set_adventureLevelCount);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "adventureLevelNames", _s_set_adventureLevelNames);
            
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 1, 0, 0);
			
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 1)
				{
					
					var gen_ret = new DreamPark.DreamParkPackageHost();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.DreamParkPackageHost constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GroupLevelCount(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _groupId = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.GroupLevelCount( _groupId );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ResolveGroupSlot(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    int _groupIndex = LuaAPI.xlua_tointeger(L, 2);
                    string _groupId = LuaAPI.lua_tostring(L, 3);
                    
                        var gen_ret = gen_to_be_invoked.ResolveGroupSlot( _groupIndex, _groupId );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ResolveGroupId(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _groupId = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.ResolveGroupId( _groupId );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_LoadLevel(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    int _slot = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.LoadLevel( _slot );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_PreloadLevel(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    int _slot = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.PreloadLevel( _slot );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsLevelReady(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    int _slot = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.IsLevelReady( _slot );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ShowOverlay(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    bool _visible = LuaAPI.lua_toboolean(L, 2);
                    
                    gen_to_be_invoked.ShowOverlay( _visible );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetOverlayAutoVisibility(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    bool _enabled = LuaAPI.lua_toboolean(L, 2);
                    
                    gen_to_be_invoked.SetOverlayAutoVisibility( _enabled );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetRoomFootprintMeters(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector2 _footprint;translator.Get(L, 2, out _footprint);
                    
                    gen_to_be_invoked.SetRoomFootprintMeters( _footprint );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ActivatePreparedLevel(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    int _slot = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.ActivatePreparedLevel( _slot );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_CurrentLevelIndex(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.CurrentLevelIndex);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsChangingLevel(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.IsChangingLevel);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LastLevelError(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.LastLevelError);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LevelCount(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.LevelCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_kind(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.kind);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_contentId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.contentId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_container(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.container);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_levelParent(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.levelParent);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_startLevel(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.startLevel);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gameOverLevel(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.gameOverLevel);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_overlayLevel(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.overlayLevel);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_transitionEffect(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.transitionEffect);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_levelLoader(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.levelLoader);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_transitionDelaySeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.transitionDelaySeconds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_overlayAutoVisibility(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.overlayAutoVisibility);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_adventureLevelCount(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.adventureLevelCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_adventureLevelNames(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.adventureLevelNames);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_kind(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                DreamPark.DreamParkPackageKind gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.kind = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_contentId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.contentId = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_container(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.container = (UnityEngine.GameObject)translator.GetObject(L, 2, typeof(UnityEngine.GameObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_levelParent(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.levelParent = (UnityEngine.Transform)translator.GetObject(L, 2, typeof(UnityEngine.Transform));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_startLevel(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.startLevel = (UnityEngine.GameObject)translator.GetObject(L, 2, typeof(UnityEngine.GameObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gameOverLevel(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gameOverLevel = (UnityEngine.GameObject)translator.GetObject(L, 2, typeof(UnityEngine.GameObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_overlayLevel(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.overlayLevel = (UnityEngine.GameObject)translator.GetObject(L, 2, typeof(UnityEngine.GameObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_transitionEffect(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.transitionEffect = (UnityEngine.GameObject)translator.GetObject(L, 2, typeof(UnityEngine.GameObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_levelLoader(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.levelLoader = (DreamPark.DreamLevelLoader)translator.GetObject(L, 2, typeof(DreamPark.DreamLevelLoader));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_transitionDelaySeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.transitionDelaySeconds = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_overlayAutoVisibility(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.overlayAutoVisibility = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_adventureLevelCount(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.adventureLevelCount = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_adventureLevelNames(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.adventureLevelNames = (System.Collections.Generic.List<string>)translator.GetObject(L, 2, typeof(System.Collections.Generic.List<string>));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_LevelActivated(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
			DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                System.Action<int> gen_delegate = translator.GetDelegate<System.Action<int>>(L, 3);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#3 need System.Action<int>!");
                }
				
				if (gen_param_count == 3)
				{
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "+")) {
						gen_to_be_invoked.LevelActivated += gen_delegate;
						return 0;
					} 
					
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "-")) {
						gen_to_be_invoked.LevelActivated -= gen_delegate;
						return 0;
					} 
					
				}
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			LuaAPI.luaL_error(L, "invalid arguments to DreamPark.DreamParkPackageHost.LevelActivated!");
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_LevelPresented(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
			DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                System.Action<int> gen_delegate = translator.GetDelegate<System.Action<int>>(L, 3);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#3 need System.Action<int>!");
                }
				
				if (gen_param_count == 3)
				{
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "+")) {
						gen_to_be_invoked.LevelPresented += gen_delegate;
						return 0;
					} 
					
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "-")) {
						gen_to_be_invoked.LevelPresented -= gen_delegate;
						return 0;
					} 
					
				}
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			LuaAPI.luaL_error(L, "invalid arguments to DreamPark.DreamParkPackageHost.LevelPresented!");
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_LevelChangeFailed(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
			DreamPark.DreamParkPackageHost gen_to_be_invoked = (DreamPark.DreamParkPackageHost)translator.FastGetCSObj(L, 1);
                System.Action<int> gen_delegate = translator.GetDelegate<System.Action<int>>(L, 3);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#3 need System.Action<int>!");
                }
				
				if (gen_param_count == 3)
				{
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "+")) {
						gen_to_be_invoked.LevelChangeFailed += gen_delegate;
						return 0;
					} 
					
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "-")) {
						gen_to_be_invoked.LevelChangeFailed -= gen_delegate;
						return 0;
					} 
					
				}
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			LuaAPI.luaL_error(L, "invalid arguments to DreamPark.DreamParkPackageHost.LevelChangeFailed!");
            return 0;
        }
        
		
		
    }
}
