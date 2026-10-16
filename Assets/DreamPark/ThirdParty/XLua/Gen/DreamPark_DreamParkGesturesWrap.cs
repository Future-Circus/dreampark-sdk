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
    public class DreamParkDreamParkGesturesWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.DreamParkGestures);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 9, 3, 2);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "DrainPending", _m_DrainPending_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ClearPending", _m_ClearPending_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Submit", _m_Submit_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ToLuaTable", _m_ToLuaTable_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "KindName", _m_KindName_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "PhaseName", _m_PhaseName_xlua_st_);
            
			Utils.RegisterFunc(L, Utils.CLS_IDX, "Routed", _e_Routed);
			
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MaxPending", DreamPark.DreamParkGestures.MaxPending);
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Enabled", _g_get_Enabled);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "RaycastCamera", _g_get_RaycastCamera);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "PendingCount", _g_get_PendingCount);
            
			Utils.RegisterFunc(L, Utils.CLS_SETTER_IDX, "Enabled", _s_set_Enabled);
            Utils.RegisterFunc(L, Utils.CLS_SETTER_IDX, "RaycastCamera", _s_set_RaycastCamera);
            
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "DreamPark.DreamParkGestures does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_DrainPending_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    
                        var gen_ret = DreamPark.DreamParkGestures.DrainPending(  );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ClearPending_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    DreamPark.DreamParkGestures.ClearPending(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Submit_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    DreamPark.DreamParkGesture _g;translator.Get(L, 1, out _g);
                    
                    DreamPark.DreamParkGestures.Submit( _g );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ToLuaTable_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    XLua.LuaEnv _env = (XLua.LuaEnv)translator.GetObject(L, 1, typeof(XLua.LuaEnv));
                    DreamPark.DreamParkRoutedGesture _r = (DreamPark.DreamParkRoutedGesture)translator.GetObject(L, 2, typeof(DreamPark.DreamParkRoutedGesture));
                    
                        var gen_ret = DreamPark.DreamParkGestures.ToLuaTable( _env, _r );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_KindName_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    DreamPark.DreamParkGestureKind _k;translator.Get(L, 1, out _k);
                    
                        var gen_ret = DreamPark.DreamParkGestures.KindName( _k );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_PhaseName_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    DreamPark.DreamParkGestureKind _k;translator.Get(L, 1, out _k);
                    
                        var gen_ret = DreamPark.DreamParkGestures.PhaseName( _k );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Enabled(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, DreamPark.DreamParkGestures.Enabled);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_RaycastCamera(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.Push(L, DreamPark.DreamParkGestures.RaycastCamera);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_PendingCount(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, DreamPark.DreamParkGestures.PendingCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_Enabled(RealStatePtr L)
        {
		    try {
                
			    DreamPark.DreamParkGestures.Enabled = LuaAPI.lua_toboolean(L, 1);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_RaycastCamera(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    DreamPark.DreamParkGestures.RaycastCamera = (UnityEngine.Camera)translator.GetObject(L, 1, typeof(UnityEngine.Camera));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_Routed(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<DreamPark.DreamParkRoutedGesture> gen_delegate = translator.GetDelegate<System.Action<DreamPark.DreamParkRoutedGesture>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<DreamPark.DreamParkRoutedGesture>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.DreamParkGestures.Routed += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.DreamParkGestures.Routed -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.DreamParkGestures.Routed!");
        }
        
    }
}
