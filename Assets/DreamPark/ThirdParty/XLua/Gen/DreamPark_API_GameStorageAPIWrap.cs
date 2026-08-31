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
    public class DreamParkAPIGameStorageAPIWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.API.GameStorageAPI);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 20, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "Slugify", _m_Slugify_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Get", _m_Get_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "IsReady", _m_IsReady_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "OnReady", _m_OnReady_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Set", _m_Set_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Increment", _m_Increment_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Max", _m_Max_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Min", _m_Min_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Delete", _m_Delete_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FlushAll", _m_FlushAll_xlua_st_);
            
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnStorageSynced", _e_OnStorageSynced);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnNumericOp", _e_OnNumericOp);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnSetOp", _e_OnSetOp);
			
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MaxKeyLength", DreamPark.API.GameStorageAPI.MaxKeyLength);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MaxStringValueBytes", DreamPark.API.GameStorageAPI.MaxStringValueBytes);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MaxKeysPerScope", DreamPark.API.GameStorageAPI.MaxKeysPerScope);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MaxAttractionScopes", DreamPark.API.GameStorageAPI.MaxAttractionScopes);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MaxScopeBytes", DreamPark.API.GameStorageAPI.MaxScopeBytes);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MaxOpsPerRequest", DreamPark.API.GameStorageAPI.MaxOpsPerRequest);
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "DreamPark.API.GameStorageAPI does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Slugify_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _attraction = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.GameStorageAPI.Slugify( _attraction );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Get_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _attraction = LuaAPI.lua_tostring(L, 2);
                    string _key = LuaAPI.lua_tostring(L, 3);
                    
                        var gen_ret = DreamPark.API.GameStorageAPI.Get( _contentId, _attraction, _key );
                        translator.PushAny(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsReady_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.GameStorageAPI.IsReady( _contentId );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_OnReady_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    System.Action _callback = translator.GetDelegate<System.Action>(L, 2);
                    
                    DreamPark.API.GameStorageAPI.OnReady( _contentId, _callback );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Set_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _attraction = LuaAPI.lua_tostring(L, 2);
                    string _key = LuaAPI.lua_tostring(L, 3);
                    object _value = translator.GetObject(L, 4, typeof(object));
                    
                        var gen_ret = DreamPark.API.GameStorageAPI.Set( _contentId, _attraction, _key, _value );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Increment_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 4)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _attraction = LuaAPI.lua_tostring(L, 2);
                    string _key = LuaAPI.lua_tostring(L, 3);
                    double _amount = LuaAPI.lua_tonumber(L, 4);
                    
                        var gen_ret = DreamPark.API.GameStorageAPI.Increment( _contentId, _attraction, _key, _amount );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _attraction = LuaAPI.lua_tostring(L, 2);
                    string _key = LuaAPI.lua_tostring(L, 3);
                    
                        var gen_ret = DreamPark.API.GameStorageAPI.Increment( _contentId, _attraction, _key );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.GameStorageAPI.Increment!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Max_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _attraction = LuaAPI.lua_tostring(L, 2);
                    string _key = LuaAPI.lua_tostring(L, 3);
                    double _value = LuaAPI.lua_tonumber(L, 4);
                    
                        var gen_ret = DreamPark.API.GameStorageAPI.Max( _contentId, _attraction, _key, _value );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Min_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _attraction = LuaAPI.lua_tostring(L, 2);
                    string _key = LuaAPI.lua_tostring(L, 3);
                    double _value = LuaAPI.lua_tonumber(L, 4);
                    
                        var gen_ret = DreamPark.API.GameStorageAPI.Min( _contentId, _attraction, _key, _value );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Delete_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _attraction = LuaAPI.lua_tostring(L, 2);
                    string _key = LuaAPI.lua_tostring(L, 3);
                    
                    DreamPark.API.GameStorageAPI.Delete( _contentId, _attraction, _key );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FlushAll_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    DreamPark.API.GameStorageAPI.FlushAll(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        
        
		
		
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnStorageSynced(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<string> gen_delegate = translator.GetDelegate<System.Action<string>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<string>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.GameStorageAPI.OnStorageSynced += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.GameStorageAPI.OnStorageSynced -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.GameStorageAPI.OnStorageSynced!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnNumericOp(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<string, string, string, string, double, double> gen_delegate = translator.GetDelegate<System.Action<string, string, string, string, double, double>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<string, string, string, string, double, double>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.GameStorageAPI.OnNumericOp += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.GameStorageAPI.OnNumericOp -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.GameStorageAPI.OnNumericOp!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnSetOp(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<string, string, string, object> gen_delegate = translator.GetDelegate<System.Action<string, string, string, object>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<string, string, string, object>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.GameStorageAPI.OnSetOp += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.GameStorageAPI.OnSetOp -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.GameStorageAPI.OnSetOp!");
        }
        
    }
}
