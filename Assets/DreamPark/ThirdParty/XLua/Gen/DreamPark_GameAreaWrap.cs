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
    public class DreamParkGameAreaWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.GameArea);
			Utils.BeginObjectRegister(type, L, translator, 0, 6, 9, 9);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ComputeBounds", _m_ComputeBounds);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "Enter", _m_Enter);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "Exit", _m_Exit);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "IsPointWithinBounds", _m_IsPointWithinBounds);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SignedDistanceTo", _m_SignedDistanceTo);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetWorldCorners", _m_GetWorldCorners);
			
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "gameId", _g_get_gameId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "resourceName", _g_get_resourceName);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "priority", _g_get_priority);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "isPlaying", _g_get_isPlaying);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "padding", _g_get_padding);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "showPaddingGizmo", _g_get_showPaddingGizmo);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "halfExtents", _g_get_halfExtents);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "unpaddedHalfExtents", _g_get_unpaddedHalfExtents);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "zoneColor", _g_get_zoneColor);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "gameId", _s_set_gameId);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "resourceName", _s_set_resourceName);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "priority", _s_set_priority);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "isPlaying", _s_set_isPlaying);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "padding", _s_set_padding);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "showPaddingGizmo", _s_set_showPaddingGizmo);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "halfExtents", _s_set_halfExtents);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "unpaddedHalfExtents", _s_set_unpaddedHalfExtents);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "zoneColor", _s_set_zoneColor);
            
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 4, 2, 2);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "FindNearest", _m_FindNearest_xlua_st_);
            
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnContentZoneChanged", _e_OnContentZoneChanged);
			
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "allGameAreas", DreamPark.GameArea.allGameAreas);
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "currentGameArea", _g_get_currentGameArea);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "VerboseLogging", _g_get_VerboseLogging);
            
			Utils.RegisterFunc(L, Utils.CLS_SETTER_IDX, "currentGameArea", _s_set_currentGameArea);
            Utils.RegisterFunc(L, Utils.CLS_SETTER_IDX, "VerboseLogging", _s_set_VerboseLogging);
            
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 1)
				{
					
					var gen_ret = new DreamPark.GameArea();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.GameArea constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ComputeBounds(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.ComputeBounds(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Enter(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.Enter(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Exit(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.Exit(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsPointWithinBounds(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector3 _worldPoint;translator.Get(L, 2, out _worldPoint);
                    
                        var gen_ret = gen_to_be_invoked.IsPointWithinBounds( _worldPoint );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SignedDistanceTo(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector3 _worldPoint;translator.Get(L, 2, out _worldPoint);
                    
                        var gen_ret = gen_to_be_invoked.SignedDistanceTo( _worldPoint );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetWorldCorners(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                        var gen_ret = gen_to_be_invoked.GetWorldCorners(  );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindNearest_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Vector3 _worldPosition;translator.Get(L, 1, out _worldPosition);
                    
                        var gen_ret = DreamPark.GameArea.FindNearest( _worldPosition );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_currentGameArea(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.Push(L, DreamPark.GameArea.currentGameArea);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_VerboseLogging(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, DreamPark.GameArea.VerboseLogging);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gameId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.gameId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_resourceName(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.resourceName);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_priority(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.priority);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_isPlaying(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.isPlaying);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_padding(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.padding);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_showPaddingGizmo(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.showPaddingGizmo);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_halfExtents(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector3(L, gen_to_be_invoked.halfExtents);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_unpaddedHalfExtents(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector3(L, gen_to_be_invoked.unpaddedHalfExtents);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_zoneColor(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineColor(L, gen_to_be_invoked.zoneColor);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_currentGameArea(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    DreamPark.GameArea.currentGameArea = (DreamPark.GameArea)translator.GetObject(L, 1, typeof(DreamPark.GameArea));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_VerboseLogging(RealStatePtr L)
        {
		    try {
                
			    DreamPark.GameArea.VerboseLogging = LuaAPI.lua_toboolean(L, 1);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gameId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gameId = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_resourceName(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.resourceName = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_priority(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.priority = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_isPlaying(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.isPlaying = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_padding(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.padding = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_showPaddingGizmo(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.showPaddingGizmo = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_halfExtents(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                UnityEngine.Vector3 gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.halfExtents = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_unpaddedHalfExtents(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                UnityEngine.Vector3 gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.unpaddedHalfExtents = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_zoneColor(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.GameArea gen_to_be_invoked = (DreamPark.GameArea)translator.FastGetCSObj(L, 1);
                UnityEngine.Color gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.zoneColor = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnContentZoneChanged(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<DreamPark.GameArea, DreamPark.GameArea> gen_delegate = translator.GetDelegate<System.Action<DreamPark.GameArea, DreamPark.GameArea>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<DreamPark.GameArea, DreamPark.GameArea>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.GameArea.OnContentZoneChanged += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.GameArea.OnContentZoneChanged -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.GameArea.OnContentZoneChanged!");
        }
        
    }
}
