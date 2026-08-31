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
    public class DreamParkLevelTemplateWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.LevelTemplate);
			Utils.BeginObjectRegister(type, L, translator, 0, 6, 21, 18);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "RegenerateFloor", _m_RegenerateFloor);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetFloorVisibilityForMode", _m_SetFloorVisibilityForMode);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ShowSelect", _m_ShowSelect);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "HideSelect", _m_HideSelect);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "RenderDimensions", _m_RenderDimensions);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "TestRealWorldCalibration", _m_TestRealWorldCalibration);
			
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "Size", _g_get_Size);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "DimensionsInFeet", _g_get_DimensionsInFeet);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "isBuildMode", _g_get_isBuildMode);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gameId", _g_get_gameId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "size", _g_get_size);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "customSize", _g_get_customSize);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "defaultAnchorPosition", _g_get_defaultAnchorPosition);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "generateFloor", _g_get_generateFloor);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "generateCeiling", _g_get_generateCeiling);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "runtimePlane", _g_get_runtimePlane);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "runtimeCeiling", _g_get_runtimeCeiling);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "renderDimensions", _g_get_renderDimensions);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "showCutoutGizmos", _g_get_showCutoutGizmos);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "showGridGizmo", _g_get_showGridGizmo);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gridDensity", _g_get_gridDensity);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gridWidth", _g_get_gridWidth);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gridHeight", _g_get_gridHeight);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gridX", _g_get_gridX);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gridY", _g_get_gridY);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "floorData", _g_get_floorData);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "floorMaterial", _g_get_floorMaterial);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "gameId", _s_set_gameId);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "size", _s_set_size);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "customSize", _s_set_customSize);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "defaultAnchorPosition", _s_set_defaultAnchorPosition);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "generateFloor", _s_set_generateFloor);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "generateCeiling", _s_set_generateCeiling);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "runtimePlane", _s_set_runtimePlane);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "runtimeCeiling", _s_set_runtimeCeiling);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "renderDimensions", _s_set_renderDimensions);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "showCutoutGizmos", _s_set_showCutoutGizmos);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "showGridGizmo", _s_set_showGridGizmo);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "gridDensity", _s_set_gridDensity);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "gridWidth", _s_set_gridWidth);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "gridHeight", _s_set_gridHeight);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "gridX", _s_set_gridX);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "gridY", _s_set_gridY);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "floorData", _s_set_floorData);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "floorMaterial", _s_set_floorMaterial);
            
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 3, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "NotifyLevelTemplateChanged", _m_NotifyLevelTemplateChanged_xlua_st_);
            
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnAnyLevelTemplateChanged", _e_OnAnyLevelTemplateChanged);
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 1)
				{
					
					var gen_ret = new DreamPark.LevelTemplate();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.LevelTemplate constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NotifyLevelTemplateChanged_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    DreamPark.LevelTemplate.NotifyLevelTemplateChanged(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RegenerateFloor(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.RegenerateFloor(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetFloorVisibilityForMode(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    bool _isBuildMode = LuaAPI.lua_toboolean(L, 2);
                    
                    gen_to_be_invoked.SetFloorVisibilityForMode( _isBuildMode );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ShowSelect(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.ShowSelect(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HideSelect(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.HideSelect(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RenderDimensions(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.RenderDimensions(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TestRealWorldCalibration(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.TestRealWorldCalibration(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Size(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector3(L, gen_to_be_invoked.Size);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_DimensionsInFeet(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.DimensionsInFeet);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_isBuildMode(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.isBuildMode);
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
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.gameId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_size(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.size);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_customSize(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.customSize);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_defaultAnchorPosition(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.defaultAnchorPosition);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_generateFloor(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.generateFloor);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_generateCeiling(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.generateCeiling);
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
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.runtimePlane);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_runtimeCeiling(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.runtimeCeiling);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_renderDimensions(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.renderDimensions);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_showCutoutGizmos(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.showCutoutGizmos);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_showGridGizmo(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.showGridGizmo);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gridDensity(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.gridDensity);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gridWidth(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.gridWidth);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gridHeight(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.gridHeight);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gridX(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.gridX);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gridY(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.gridY);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_floorData(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.floorData);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_floorMaterial(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.floorMaterial);
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
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gameId = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_size(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                DreamPark.GameLevelSize gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.size = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_customSize(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                UnityEngine.Vector2 gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.customSize = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_defaultAnchorPosition(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                UnityEngine.Vector2 gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.defaultAnchorPosition = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_generateFloor(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.generateFloor = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_generateCeiling(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.generateCeiling = LuaAPI.lua_toboolean(L, 2);
            
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
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.runtimePlane = (UnityEngine.GameObject)translator.GetObject(L, 2, typeof(UnityEngine.GameObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_runtimeCeiling(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.runtimeCeiling = (UnityEngine.GameObject)translator.GetObject(L, 2, typeof(UnityEngine.GameObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_renderDimensions(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.renderDimensions = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_showCutoutGizmos(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.showCutoutGizmos = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_showGridGizmo(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.showGridGizmo = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gridDensity(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gridDensity = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gridWidth(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gridWidth = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gridHeight(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gridHeight = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gridX(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gridX = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gridY(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gridY = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_floorData(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.floorData = (Defective.JSON.JSONObject)translator.GetObject(L, 2, typeof(Defective.JSON.JSONObject));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_floorMaterial(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.LevelTemplate gen_to_be_invoked = (DreamPark.LevelTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.floorMaterial = (UnityEngine.Material)translator.GetObject(L, 2, typeof(UnityEngine.Material));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnAnyLevelTemplateChanged(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action gen_delegate = translator.GetDelegate<System.Action>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.LevelTemplate.OnAnyLevelTemplateChanged += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.LevelTemplate.OnAnyLevelTemplateChanged -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.LevelTemplate.OnAnyLevelTemplateChanged!");
        }
        
    }
}
