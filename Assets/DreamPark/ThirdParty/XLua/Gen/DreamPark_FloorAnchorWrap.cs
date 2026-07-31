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
    public class DreamParkFloorAnchorWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.FloorAnchor);
			Utils.BeginObjectRegister(type, L, translator, 0, 8, 16, 15);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "RecacheCorners", _m_RecacheCorners);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "PrecalculateBounds", _m_PrecalculateBounds);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "HasPrecalculatedBounds", _m_HasPrecalculatedBounds);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetPrecalculatedSize", _m_GetPrecalculatedSize);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetCenter", _m_GetCenter);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetPrecalculatedCornersWorld", _m_GetPrecalculatedCornersWorld);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetPrecalculatedFloorCornersWorld", _m_GetPrecalculatedFloorCornersWorld);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "Update", _m_Update);
			
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "isBuildMode", _g_get_isBuildMode);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "floorMeshFilter", _g_get_floorMeshFilter);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "floorTransform", _g_get_floorTransform);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "calibrator", _g_get_calibrator);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "localOffset", _g_get_localOffset);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "autoFindFloor", _g_get_autoFindFloor);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "matchGrade", _g_get_matchGrade);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "debugDrawBounds", _g_get_debugDrawBounds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "debugLogValues", _g_get_debugLogValues);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "fixedHeightOffset", _g_get_fixedHeightOffset);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "useFixedHeight", _g_get_useFixedHeight);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "excludeLayerName", _g_get_excludeLayerName);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "includeInactiveRenderers", _g_get_includeInactiveRenderers);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "maxHeightDeviation", _g_get_maxHeightDeviation);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "maxVertexDistance", _g_get_maxVertexDistance);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "debugOutlierFiltering", _g_get_debugOutlierFiltering);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "floorMeshFilter", _s_set_floorMeshFilter);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "floorTransform", _s_set_floorTransform);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "calibrator", _s_set_calibrator);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "localOffset", _s_set_localOffset);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "autoFindFloor", _s_set_autoFindFloor);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "matchGrade", _s_set_matchGrade);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "debugDrawBounds", _s_set_debugDrawBounds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "debugLogValues", _s_set_debugLogValues);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "fixedHeightOffset", _s_set_fixedHeightOffset);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "useFixedHeight", _s_set_useFixedHeight);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "excludeLayerName", _s_set_excludeLayerName);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "includeInactiveRenderers", _s_set_includeInactiveRenderers);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "maxHeightDeviation", _s_set_maxHeightDeviation);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "maxVertexDistance", _s_set_maxVertexDistance);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "debugOutlierFiltering", _s_set_debugOutlierFiltering);
            
			
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
					
					var gen_ret = new DreamPark.FloorAnchor();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.FloorAnchor constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RecacheCorners(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.RecacheCorners(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_PrecalculateBounds(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.PrecalculateBounds(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasPrecalculatedBounds(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                        var gen_ret = gen_to_be_invoked.HasPrecalculatedBounds(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetPrecalculatedSize(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                        var gen_ret = gen_to_be_invoked.GetPrecalculatedSize(  );
                        translator.PushUnityEngineVector3(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetCenter(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                        var gen_ret = gen_to_be_invoked.GetCenter(  );
                        translator.PushUnityEngineVector3(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetPrecalculatedCornersWorld(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                        var gen_ret = gen_to_be_invoked.GetPrecalculatedCornersWorld(  );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetPrecalculatedFloorCornersWorld(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                        var gen_ret = gen_to_be_invoked.GetPrecalculatedFloorCornersWorld(  );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Update(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.Update(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_isBuildMode(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.isBuildMode);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_floorMeshFilter(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.floorMeshFilter);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_floorTransform(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.floorTransform);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_calibrator(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.calibrator);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_localOffset(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector3(L, gen_to_be_invoked.localOffset);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_autoFindFloor(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.autoFindFloor);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_matchGrade(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.matchGrade);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_debugDrawBounds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.debugDrawBounds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_debugLogValues(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.debugLogValues);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_fixedHeightOffset(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.fixedHeightOffset);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_useFixedHeight(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.useFixedHeight);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_excludeLayerName(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.excludeLayerName);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_includeInactiveRenderers(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.includeInactiveRenderers);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_maxHeightDeviation(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.maxHeightDeviation);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_maxVertexDistance(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.maxVertexDistance);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_debugOutlierFiltering(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.debugOutlierFiltering);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_floorMeshFilter(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.floorMeshFilter = (UnityEngine.MeshFilter)translator.GetObject(L, 2, typeof(UnityEngine.MeshFilter));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_floorTransform(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.floorTransform = (UnityEngine.Transform)translator.GetObject(L, 2, typeof(UnityEngine.Transform));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_calibrator(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.calibrator = (DreamPark.CalibrateLevel)translator.GetObject(L, 2, typeof(DreamPark.CalibrateLevel));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_localOffset(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                UnityEngine.Vector3 gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.localOffset = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_autoFindFloor(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.autoFindFloor = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_matchGrade(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.matchGrade = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_debugDrawBounds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.debugDrawBounds = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_debugLogValues(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.debugLogValues = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_fixedHeightOffset(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.fixedHeightOffset = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_useFixedHeight(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.useFixedHeight = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_excludeLayerName(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.excludeLayerName = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_includeInactiveRenderers(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.includeInactiveRenderers = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_maxHeightDeviation(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.maxHeightDeviation = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_maxVertexDistance(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.maxVertexDistance = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_debugOutlierFiltering(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.FloorAnchor gen_to_be_invoked = (DreamPark.FloorAnchor)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.debugOutlierFiltering = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
		
		
    }
}
