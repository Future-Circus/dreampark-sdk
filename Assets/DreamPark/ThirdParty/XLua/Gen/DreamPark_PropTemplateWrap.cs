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
    public class DreamParkPropTemplateWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.PropTemplate);
			Utils.BeginObjectRegister(type, L, translator, 0, 6, 13, 11);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "NotifyChanged", _m_NotifyChanged);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ApplyCalibrationYOffset", _m_ApplyCalibrationYOffset);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "CompileCalibrationData", _m_CompileCalibrationData);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ApplyCalibrationData", _m_ApplyCalibrationData);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "TryGetWorldFootprint", _m_TryGetWorldFootprint);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "TryGetWorldCutoutPolygons", _m_TryGetWorldCutoutPolygons);
			
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "SurfaceHeight", _g_get_SurfaceHeight);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "IsNestedUnderTemplate", _g_get_IsNestedUnderTemplate);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gameId", _g_get_gameId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "resourceName", _g_get_resourceName);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "category", _g_get_category);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "affectsGapFiller", _g_get_affectsGapFiller);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "cutGapFillerHole", _g_get_cutGapFillerHole);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "useColliderBounds", _g_get_useColliderBounds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "customFootprintMeters", _g_get_customFootprintMeters);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "footprintOffsetMeters", _g_get_footprintOffsetMeters);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "showFootprintGizmos", _g_get_showFootprintGizmos);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "pointData", _g_get_pointData);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "runtimePlane", _g_get_runtimePlane);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "gameId", _s_set_gameId);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "resourceName", _s_set_resourceName);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "category", _s_set_category);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "affectsGapFiller", _s_set_affectsGapFiller);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "cutGapFillerHole", _s_set_cutGapFillerHole);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "useColliderBounds", _s_set_useColliderBounds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "customFootprintMeters", _s_set_customFootprintMeters);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "footprintOffsetMeters", _s_set_footprintOffsetMeters);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "showFootprintGizmos", _s_set_showFootprintGizmos);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "pointData", _s_set_pointData);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "runtimePlane", _s_set_runtimePlane);
            
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 3, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "NotifyPropTemplateChanged", _m_NotifyPropTemplateChanged_xlua_st_);
            
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnAnyPropTemplateChanged", _e_OnAnyPropTemplateChanged);
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 1)
				{
					
					var gen_ret = new DreamPark.PropTemplate();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.PropTemplate constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NotifyPropTemplateChanged_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    DreamPark.PropTemplate.NotifyPropTemplateChanged(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NotifyChanged(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.NotifyChanged(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyCalibrationYOffset(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    float _yOffset = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    gen_to_be_invoked.ApplyCalibrationYOffset( _yOffset );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CompileCalibrationData(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                        var gen_ret = gen_to_be_invoked.CompileCalibrationData(  );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyCalibrationData(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    Defective.JSON.JSONObject _calibrationData = (Defective.JSON.JSONObject)translator.GetObject(L, 2, typeof(Defective.JSON.JSONObject));
                    
                    gen_to_be_invoked.ApplyCalibrationData( _calibrationData );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TryGetWorldFootprint(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector2[] _worldFootprint;
                    float _surfaceHeight;
                    
                        var gen_ret = gen_to_be_invoked.TryGetWorldFootprint( out _worldFootprint, out _surfaceHeight );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    translator.Push(L, _worldFootprint);
                        
                    LuaAPI.lua_pushnumber(L, _surfaceHeight);
                        
                    
                    
                    
                    return 3;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TryGetWorldCutoutPolygons(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    System.Collections.Generic.List<UnityEngine.Vector2[]> _worldCutouts;
                    
                        var gen_ret = gen_to_be_invoked.TryGetWorldCutoutPolygons( out _worldCutouts );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    translator.Push(L, _worldCutouts);
                        
                    
                    
                    
                    return 2;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_SurfaceHeight(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.SurfaceHeight);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsNestedUnderTemplate(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.IsNestedUnderTemplate);
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
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
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
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.resourceName);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_category(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.category);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_affectsGapFiller(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.affectsGapFiller);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_cutGapFillerHole(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.cutGapFillerHole);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_useColliderBounds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.useColliderBounds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_customFootprintMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.customFootprintMeters);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_footprintOffsetMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.footprintOffsetMeters);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_showFootprintGizmos(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.showFootprintGizmos);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_pointData(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.pointData);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_runtimePlane(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.runtimePlane);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gameId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
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
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.resourceName = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_category(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                DreamPark.PropCategory gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.category = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_affectsGapFiller(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.affectsGapFiller = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_cutGapFillerHole(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.cutGapFillerHole = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_useColliderBounds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.useColliderBounds = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_customFootprintMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                UnityEngine.Vector2 gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.customFootprintMeters = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_footprintOffsetMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                UnityEngine.Vector2 gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.footprintOffsetMeters = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_showFootprintGizmos(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.showFootprintGizmos = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_pointData(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.pointData = (Defective.JSON.JSONObject)translator.GetObject(L, 2, typeof(Defective.JSON.JSONObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_runtimePlane(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.PropTemplate gen_to_be_invoked = (DreamPark.PropTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.runtimePlane = (UnityEngine.GameObject)translator.GetObject(L, 2, typeof(UnityEngine.GameObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnAnyPropTemplateChanged(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action gen_delegate = translator.GetDelegate<System.Action>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.PropTemplate.OnAnyPropTemplateChanged += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.PropTemplate.OnAnyPropTemplateChanged -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.PropTemplate.OnAnyPropTemplateChanged!");
        }
        
    }
}
