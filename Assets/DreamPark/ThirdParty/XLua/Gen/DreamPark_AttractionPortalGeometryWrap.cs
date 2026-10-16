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
    public class DreamParkAttractionPortalGeometryWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.AttractionPortalGeometry);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 9, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "Wrap", _m_Wrap_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "LocalPoint", _m_LocalPoint_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EdgeOf", _m_EdgeOf_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "OutwardNormal", _m_OutwardNormal_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "WalkDirection", _m_WalkDirection_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FromLocalPoint", _m_FromLocalPoint_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EdgeLabel", _m_EdgeLabel_xlua_st_);
            
			
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "DefaultEntryPosition", DreamPark.AttractionPortalGeometry.DefaultEntryPosition);
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "DreamPark.AttractionPortalGeometry does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Wrap_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _t = (float)LuaAPI.lua_tonumber(L, 1);
                    
                        var gen_ret = DreamPark.AttractionPortalGeometry.Wrap( _t );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_LocalPoint_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    float _t = (float)LuaAPI.lua_tonumber(L, 1);
                    UnityEngine.Vector2 _footprintMeters;translator.Get(L, 2, out _footprintMeters);
                    
                        var gen_ret = DreamPark.AttractionPortalGeometry.LocalPoint( _t, _footprintMeters );
                        translator.PushUnityEngineVector3(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EdgeOf_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    float _t = (float)LuaAPI.lua_tonumber(L, 1);
                    
                        var gen_ret = DreamPark.AttractionPortalGeometry.EdgeOf( _t );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_OutwardNormal_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    float _t = (float)LuaAPI.lua_tonumber(L, 1);
                    
                        var gen_ret = DreamPark.AttractionPortalGeometry.OutwardNormal( _t );
                        translator.PushUnityEngineVector3(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_WalkDirection_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    DreamPark.AttractionPortal _portal = (DreamPark.AttractionPortal)translator.GetObject(L, 1, typeof(DreamPark.AttractionPortal));
                    
                        var gen_ret = DreamPark.AttractionPortalGeometry.WalkDirection( _portal );
                        translator.PushUnityEngineVector3(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FromLocalPoint_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Vector3 _local;translator.Get(L, 1, out _local);
                    UnityEngine.Vector2 _footprintMeters;translator.Get(L, 2, out _footprintMeters);
                    
                        var gen_ret = DreamPark.AttractionPortalGeometry.FromLocalPoint( _local, _footprintMeters );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EdgeLabel_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _t = (float)LuaAPI.lua_tonumber(L, 1);
                    
                        var gen_ret = DreamPark.AttractionPortalGeometry.EdgeLabel( _t );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        
        
		
		
		
		
    }
}
