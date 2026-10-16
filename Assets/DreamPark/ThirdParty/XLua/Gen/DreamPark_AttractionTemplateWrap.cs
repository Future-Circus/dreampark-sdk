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
    public class DreamParkAttractionTemplateWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.AttractionTemplate);
			Utils.BeginObjectRegister(type, L, translator, 0, 33, 35, 9);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetPackingFootprintMeters", _m_GetPackingFootprintMeters);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetSafeFootprintMeters", _m_GetSafeFootprintMeters);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ApplyPackingVariant", _m_ApplyPackingVariant);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetPackingPoseLocalPosition", _m_GetPackingPoseLocalPosition);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ResetPackingVariant", _m_ResetPackingVariant);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetPackingPreview", _m_SetPackingPreview);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "FindFrame", _m_FindFrame);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "FrameHasValidBake", _m_FrameHasValidBake);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetFrameEnvelopes", _m_GetFrameEnvelopes);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "TryGetFrameEnvelope", _m_TryGetFrameEnvelope);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "TrySelectFrame", _m_TrySelectFrame);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ApplyLayout", _m_ApplyLayout);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ApplyFootprint", _m_ApplyFootprint);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetFrameFootprintMeters", _m_GetFrameFootprintMeters);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetLayoutPreview", _m_SetLayoutPreview);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "TryResolveLayoutPreview", _m_TryResolveLayoutPreview);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetLayoutFrames", _m_SetLayoutFrames);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetLayoutBaseStates", _m_SetLayoutBaseStates);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetShowPackingGizmos", _m_SetShowPackingGizmos);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetPortals", _m_GetPortals);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "HasEntry", _m_HasEntry);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "GetPortalWorldPose", _m_GetPortalWorldPose);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetPortals", _m_SetPortals);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetPackingBake", _m_SetPackingBake);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "FindWallLayer", _m_FindWallLayer);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "FindWallLayerById", _m_FindWallLayerById);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "HasWall", _m_HasWall);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "HasWallNamed", _m_HasWallNamed);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetWalls", _m_SetWalls);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetLayoutPreviewWalls", _m_SetLayoutPreviewWalls);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "SetWallLayers", _m_SetWallLayers);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "LayoutApplied", _e_LayoutApplied);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "WallsApplied", _e_WallsApplied);
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "safeArea", _g_get_safeArea);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "PackingBake", _g_get_PackingBake);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "HasPackingBake", _g_get_HasPackingBake);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "HasStalePackingBake", _g_get_HasStalePackingBake);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "PackingPreviewScale", _g_get_PackingPreviewScale);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "PackingPreviewEssentialOnly", _g_get_PackingPreviewEssentialOnly);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "ShowPackingGizmos", _g_get_ShowPackingGizmos);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "RuntimeFootprintMeters", _g_get_RuntimeFootprintMeters);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "LayoutFrames", _g_get_LayoutFrames);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "LayoutBaseStates", _g_get_LayoutBaseStates);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "HasLayoutFrames", _g_get_HasLayoutFrames);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "MinimumFrame", _g_get_MinimumFrame);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "LegacyEssentialMigrated", _g_get_LegacyEssentialMigrated);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "CurrentFrameId", _g_get_CurrentFrameId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "CurrentFrameScale", _g_get_CurrentFrameScale);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "LayoutPreviewFootprintMeters", _g_get_LayoutPreviewFootprintMeters);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "LayoutPreviewFrameId", _g_get_LayoutPreviewFrameId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "HasLayoutPreview", _g_get_HasLayoutPreview);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "LayoutPreviewWalls", _g_get_LayoutPreviewWalls);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "IsDreamSequenceCompatible", _g_get_IsDreamSequenceCompatible);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "MinimumFootprintMeters", _g_get_MinimumFootprintMeters);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "Portals", _g_get_Portals);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "CurrentPortals", _g_get_CurrentPortals);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "WallLayers", _g_get_WallLayers);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "HasWallLayers", _g_get_HasWallLayers);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "WallLayerSides", _g_get_WallLayerSides);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "Walls", _g_get_Walls);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "WallsWire", _g_get_WallsWire);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gameRequiresAttraction", _g_get_gameRequiresAttraction);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "safeAreaInset", _g_get_safeAreaInset);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "essentialProps", _g_get_essentialProps);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "maxGrowthScale", _g_get_maxGrowthScale);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "shrinkClearanceMeters", _g_get_shrinkClearanceMeters);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "growGroupGapMeters", _g_get_growGroupGapMeters);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "growAlignmentToleranceMeters", _g_get_growAlignmentToleranceMeters);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "safeArea", _s_set_safeArea);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "LegacyEssentialMigrated", _s_set_LegacyEssentialMigrated);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "gameRequiresAttraction", _s_set_gameRequiresAttraction);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "safeAreaInset", _s_set_safeAreaInset);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "essentialProps", _s_set_essentialProps);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "maxGrowthScale", _s_set_maxGrowthScale);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "shrinkClearanceMeters", _s_set_shrinkClearanceMeters);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "growGroupGapMeters", _s_set_growGroupGapMeters);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "growAlignmentToleranceMeters", _s_set_growAlignmentToleranceMeters);
            
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 5, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "IsValidFrameId", _m_IsValidFrameId_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AnchorToSide", _m_AnchorToSide_xlua_st_);
            
			
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "BaseFrameId", DreamPark.AttractionTemplate.BaseFrameId);
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MinimumFrameId", DreamPark.AttractionTemplate.MinimumFrameId);
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 1)
				{
					
					var gen_ret = new DreamPark.AttractionTemplate();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.AttractionTemplate constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetPackingFootprintMeters(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector2 _packingScale;translator.Get(L, 2, out _packingScale);
                    bool _essentialOnly = LuaAPI.lua_toboolean(L, 3);
                    
                        var gen_ret = gen_to_be_invoked.GetPackingFootprintMeters( _packingScale, _essentialOnly );
                        translator.PushUnityEngineVector2(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetSafeFootprintMeters(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)) 
                {
                    float _insetOverride = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.GetSafeFootprintMeters( _insetOverride );
                        translator.PushUnityEngineVector2(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 1) 
                {
                    
                        var gen_ret = gen_to_be_invoked.GetSafeFootprintMeters(  );
                        translator.PushUnityEngineVector2(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.AttractionTemplate.GetSafeFootprintMeters!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyPackingVariant(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector2 _packingScale;translator.Get(L, 2, out _packingScale);
                    bool _essentialOnly = LuaAPI.lua_toboolean(L, 3);
                    
                        var gen_ret = gen_to_be_invoked.ApplyPackingVariant( _packingScale, _essentialOnly );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetPackingPoseLocalPosition(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    DreamPark.AttractionPropPackingPose _pose = (DreamPark.AttractionPropPackingPose)translator.GetObject(L, 2, typeof(DreamPark.AttractionPropPackingPose));
                    UnityEngine.Vector2 _packingScale;translator.Get(L, 3, out _packingScale);
                    bool _essentialOnly = LuaAPI.lua_toboolean(L, 4);
                    
                        var gen_ret = gen_to_be_invoked.GetPackingPoseLocalPosition( _pose, _packingScale, _essentialOnly );
                        translator.PushUnityEngineVector3(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ResetPackingVariant(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.ResetPackingVariant(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetPackingPreview(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector2 _scale;translator.Get(L, 2, out _scale);
                    bool _essentialOnly = LuaAPI.lua_toboolean(L, 3);
                    
                    gen_to_be_invoked.SetPackingPreview( _scale, _essentialOnly );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindFrame(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _id = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.FindFrame( _id );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IsValidFrameId_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _id = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.AttractionTemplate.IsValidFrameId( _id );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FrameHasValidBake(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    DreamPark.AttractionLayoutFrame _frame = (DreamPark.AttractionLayoutFrame)translator.GetObject(L, 2, typeof(DreamPark.AttractionLayoutFrame));
                    
                        var gen_ret = gen_to_be_invoked.FrameHasValidBake( _frame );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFrameEnvelopes(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                        var gen_ret = gen_to_be_invoked.GetFrameEnvelopes(  );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TryGetFrameEnvelope(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _frameId = LuaAPI.lua_tostring(L, 2);
                    DreamPark.AttractionFrameEnvelope _envelope;
                    
                        var gen_ret = gen_to_be_invoked.TryGetFrameEnvelope( _frameId, out _envelope );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    translator.Push(L, _envelope);
                        
                    
                    
                    
                    return 2;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TrySelectFrame(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector2 _targetMeters;translator.Get(L, 2, out _targetMeters);
                    string _frameId;
                    UnityEngine.Vector2 _frameScale;
                    
                        var gen_ret = gen_to_be_invoked.TrySelectFrame( _targetMeters, out _frameId, out _frameScale );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    LuaAPI.lua_pushstring(L, _frameId);
                        
                    translator.PushUnityEngineVector2(L, _frameScale);
                        
                    
                    
                    
                    return 3;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyLayout(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& translator.Assignable<UnityEngine.Vector2>(L, 3)) 
                {
                    string _frameId = LuaAPI.lua_tostring(L, 2);
                    UnityEngine.Vector2 _frameScale;translator.Get(L, 3, out _frameScale);
                    
                        var gen_ret = gen_to_be_invoked.ApplyLayout( _frameId, _frameScale );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& translator.Assignable<UnityEngine.Vector2>(L, 3)&& translator.Assignable<DreamPark.AttractionWallMask>(L, 4)) 
                {
                    string _frameId = LuaAPI.lua_tostring(L, 2);
                    UnityEngine.Vector2 _frameScale;translator.Get(L, 3, out _frameScale);
                    DreamPark.AttractionWallMask _walls;translator.Get(L, 4, out _walls);
                    
                        var gen_ret = gen_to_be_invoked.ApplyLayout( _frameId, _frameScale, _walls );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.AttractionTemplate.ApplyLayout!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ApplyFootprint(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector2 _targetMeters;translator.Get(L, 2, out _targetMeters);
                    
                        var gen_ret = gen_to_be_invoked.ApplyFootprint( _targetMeters );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFrameFootprintMeters(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _frameId = LuaAPI.lua_tostring(L, 2);
                    UnityEngine.Vector2 _frameScale;translator.Get(L, 3, out _frameScale);
                    
                        var gen_ret = gen_to_be_invoked.GetFrameFootprintMeters( _frameId, _frameScale );
                        translator.PushUnityEngineVector2(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetLayoutPreview(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    UnityEngine.Vector2 _targetMeters;translator.Get(L, 2, out _targetMeters);
                    string _frameIdOrEmpty = LuaAPI.lua_tostring(L, 3);
                    
                    gen_to_be_invoked.SetLayoutPreview( _targetMeters, _frameIdOrEmpty );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_TryResolveLayoutPreview(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _frameId;
                    UnityEngine.Vector2 _frameScale;
                    
                        var gen_ret = gen_to_be_invoked.TryResolveLayoutPreview( out _frameId, out _frameScale );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    LuaAPI.lua_pushstring(L, _frameId);
                        
                    translator.PushUnityEngineVector2(L, _frameScale);
                        
                    
                    
                    
                    return 3;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetLayoutFrames(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    System.Collections.Generic.List<DreamPark.AttractionLayoutFrame> _frames = (System.Collections.Generic.List<DreamPark.AttractionLayoutFrame>)translator.GetObject(L, 2, typeof(System.Collections.Generic.List<DreamPark.AttractionLayoutFrame>));
                    
                    gen_to_be_invoked.SetLayoutFrames( _frames );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetLayoutBaseStates(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    System.Collections.Generic.List<DreamPark.AttractionLayoutBaseState> _states = (System.Collections.Generic.List<DreamPark.AttractionLayoutBaseState>)translator.GetObject(L, 2, typeof(System.Collections.Generic.List<DreamPark.AttractionLayoutBaseState>));
                    
                    gen_to_be_invoked.SetLayoutBaseStates( _states );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetShowPackingGizmos(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    bool _value = LuaAPI.lua_toboolean(L, 2);
                    
                    gen_to_be_invoked.SetShowPackingGizmos( _value );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetPortals(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _frameId = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.GetPortals( _frameId );
                        translator.PushAny(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasEntry(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    string _frameId = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.HasEntry( _frameId );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 1) 
                {
                    
                        var gen_ret = gen_to_be_invoked.HasEntry(  );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.AttractionTemplate.HasEntry!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetPortalWorldPose(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& translator.Assignable<DreamPark.AttractionPortal>(L, 2)) 
                {
                    DreamPark.AttractionPortal _portal = (DreamPark.AttractionPortal)translator.GetObject(L, 2, typeof(DreamPark.AttractionPortal));
                    UnityEngine.Vector3 _position;
                    UnityEngine.Vector3 _walkDirection;
                    
                    gen_to_be_invoked.GetPortalWorldPose( _portal, out _position, out _walkDirection );
                    translator.PushUnityEngineVector3(L, _position);
                        
                    translator.PushUnityEngineVector3(L, _walkDirection);
                        
                    
                    
                    
                    return 2;
                }
                if(gen_param_count == 3&& translator.Assignable<DreamPark.AttractionPortal>(L, 2)&& translator.Assignable<UnityEngine.Vector2>(L, 3)) 
                {
                    DreamPark.AttractionPortal _portal = (DreamPark.AttractionPortal)translator.GetObject(L, 2, typeof(DreamPark.AttractionPortal));
                    UnityEngine.Vector2 _footprintMeters;translator.Get(L, 3, out _footprintMeters);
                    UnityEngine.Vector3 _position;
                    UnityEngine.Vector3 _walkDirection;
                    
                    gen_to_be_invoked.GetPortalWorldPose( _portal, _footprintMeters, out _position, out _walkDirection );
                    translator.PushUnityEngineVector3(L, _position);
                        
                    translator.PushUnityEngineVector3(L, _walkDirection);
                        
                    
                    
                    
                    return 2;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.AttractionTemplate.GetPortalWorldPose!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetPortals(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    System.Collections.Generic.List<DreamPark.AttractionPortal> _value = (System.Collections.Generic.List<DreamPark.AttractionPortal>)translator.GetObject(L, 2, typeof(System.Collections.Generic.List<DreamPark.AttractionPortal>));
                    
                    gen_to_be_invoked.SetPortals( _value );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetPackingBake(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    DreamPark.AttractionPackingBake _value = (DreamPark.AttractionPackingBake)translator.GetObject(L, 2, typeof(DreamPark.AttractionPackingBake));
                    
                    gen_to_be_invoked.SetPackingBake( _value );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindWallLayer(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    DreamPark.AttractionWallSide _side;translator.Get(L, 2, out _side);
                    
                        var gen_ret = gen_to_be_invoked.FindWallLayer( _side );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindWallLayerById(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _id = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.FindWallLayerById( _id );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasWall(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    DreamPark.AttractionWallSide _side;translator.Get(L, 2, out _side);
                    
                        var gen_ret = gen_to_be_invoked.HasWall( _side );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasWallNamed(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _side = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = gen_to_be_invoked.HasWallNamed( _side );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetWalls(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    DreamPark.AttractionWallMask _walls;translator.Get(L, 2, out _walls);
                    
                    gen_to_be_invoked.SetWalls( _walls );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetLayoutPreviewWalls(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    DreamPark.AttractionWallMask _walls;translator.Get(L, 2, out _walls);
                    
                    gen_to_be_invoked.SetLayoutPreviewWalls( _walls );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetWallLayers(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    System.Collections.Generic.List<DreamPark.AttractionWallLayer> _layers = (System.Collections.Generic.List<DreamPark.AttractionWallLayer>)translator.GetObject(L, 2, typeof(System.Collections.Generic.List<DreamPark.AttractionWallLayer>));
                    
                    gen_to_be_invoked.SetWallLayers( _layers );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AnchorToSide_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Vector3 _authored;translator.Get(L, 1, out _authored);
                    DreamPark.AttractionWallLayer _layer = (DreamPark.AttractionWallLayer)translator.GetObject(L, 2, typeof(DreamPark.AttractionWallLayer));
                    UnityEngine.Vector2 _footprintMeters;translator.Get(L, 3, out _footprintMeters);
                    
                        var gen_ret = DreamPark.AttractionTemplate.AnchorToSide( _authored, _layer, _footprintMeters );
                        translator.PushUnityEngineVector3(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_safeArea(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.safeArea);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_PackingBake(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.PackingBake);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_HasPackingBake(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.HasPackingBake);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_HasStalePackingBake(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.HasStalePackingBake);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_PackingPreviewScale(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.PackingPreviewScale);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_PackingPreviewEssentialOnly(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.PackingPreviewEssentialOnly);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_ShowPackingGizmos(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.ShowPackingGizmos);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_RuntimeFootprintMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.RuntimeFootprintMeters);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LayoutFrames(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushAny(L, gen_to_be_invoked.LayoutFrames);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LayoutBaseStates(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushAny(L, gen_to_be_invoked.LayoutBaseStates);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_HasLayoutFrames(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.HasLayoutFrames);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_MinimumFrame(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.MinimumFrame);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LegacyEssentialMigrated(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.LegacyEssentialMigrated);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_CurrentFrameId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.CurrentFrameId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_CurrentFrameScale(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.CurrentFrameScale);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LayoutPreviewFootprintMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.LayoutPreviewFootprintMeters);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LayoutPreviewFrameId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.LayoutPreviewFrameId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_HasLayoutPreview(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.HasLayoutPreview);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_LayoutPreviewWalls(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushDreamParkAttractionWallMask(L, gen_to_be_invoked.LayoutPreviewWalls);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsDreamSequenceCompatible(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.IsDreamSequenceCompatible);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_MinimumFootprintMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.MinimumFootprintMeters);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Portals(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushAny(L, gen_to_be_invoked.Portals);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_CurrentPortals(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushAny(L, gen_to_be_invoked.CurrentPortals);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_WallLayers(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushAny(L, gen_to_be_invoked.WallLayers);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_HasWallLayers(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.HasWallLayers);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_WallLayerSides(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.WallLayerSides);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Walls(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushDreamParkAttractionWallMask(L, gen_to_be_invoked.Walls);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_WallsWire(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.WallsWire);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gameRequiresAttraction(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.gameRequiresAttraction);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_safeAreaInset(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.safeAreaInset);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_essentialProps(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.essentialProps);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_maxGrowthScale(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                translator.PushUnityEngineVector2(L, gen_to_be_invoked.maxGrowthScale);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_shrinkClearanceMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.shrinkClearanceMeters);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_growGroupGapMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.growGroupGapMeters);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_growAlignmentToleranceMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.growAlignmentToleranceMeters);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_safeArea(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.safeArea = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_LegacyEssentialMigrated(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.LegacyEssentialMigrated = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gameRequiresAttraction(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gameRequiresAttraction = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_safeAreaInset(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.safeAreaInset = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_essentialProps(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.essentialProps = (System.Collections.Generic.List<UnityEngine.Transform>)translator.GetObject(L, 2, typeof(System.Collections.Generic.List<UnityEngine.Transform>));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_maxGrowthScale(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                UnityEngine.Vector2 gen_value;translator.Get(L, 2, out gen_value);
				gen_to_be_invoked.maxGrowthScale = gen_value;
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_shrinkClearanceMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.shrinkClearanceMeters = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_growGroupGapMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.growGroupGapMeters = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_growAlignmentToleranceMeters(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.growAlignmentToleranceMeters = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_LayoutApplied(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
			DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                System.Action<DreamPark.AttractionTemplate> gen_delegate = translator.GetDelegate<System.Action<DreamPark.AttractionTemplate>>(L, 3);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#3 need System.Action<DreamPark.AttractionTemplate>!");
                }
				
				if (gen_param_count == 3)
				{
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "+")) {
						gen_to_be_invoked.LayoutApplied += gen_delegate;
						return 0;
					} 
					
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "-")) {
						gen_to_be_invoked.LayoutApplied -= gen_delegate;
						return 0;
					} 
					
				}
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			LuaAPI.luaL_error(L, "invalid arguments to DreamPark.AttractionTemplate.LayoutApplied!");
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_WallsApplied(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
			DreamPark.AttractionTemplate gen_to_be_invoked = (DreamPark.AttractionTemplate)translator.FastGetCSObj(L, 1);
                System.Action<DreamPark.AttractionTemplate> gen_delegate = translator.GetDelegate<System.Action<DreamPark.AttractionTemplate>>(L, 3);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#3 need System.Action<DreamPark.AttractionTemplate>!");
                }
				
				if (gen_param_count == 3)
				{
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "+")) {
						gen_to_be_invoked.WallsApplied += gen_delegate;
						return 0;
					} 
					
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "-")) {
						gen_to_be_invoked.WallsApplied -= gen_delegate;
						return 0;
					} 
					
				}
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			LuaAPI.luaL_error(L, "invalid arguments to DreamPark.AttractionTemplate.WallsApplied!");
            return 0;
        }
        
		
		
    }
}
