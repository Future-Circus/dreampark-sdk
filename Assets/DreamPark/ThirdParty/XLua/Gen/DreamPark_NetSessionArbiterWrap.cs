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
    public class DreamParkNetSessionArbiterWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.NetSessionArbiter);
			Utils.BeginObjectRegister(type, L, translator, 0, 1, 16, 8);
			
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "OnStateChanged", _e_OnStateChanged);
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "State", _g_get_State);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "HostId", _g_get_HostId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "CurrentHostId", _g_get_CurrentHostId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "HostAdvertisedCap", _g_get_HostAdvertisedCap);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "IsHost", _g_get_IsHost);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "HostedPeerCount", _g_get_HostedPeerCount);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "CanHost", _g_get_CanHost);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "Channel", _g_get_Channel);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "parkId", _g_get_parkId);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "allowHosting", _g_get_allowHosting);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "listenBaseSeconds", _g_get_listenBaseSeconds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "beaconStaleSeconds", _g_get_beaconStaleSeconds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "reelectionStepSeconds", _g_get_reelectionStepSeconds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "joinTimeoutSeconds", _g_get_joinTimeoutSeconds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "kioskGraceSeconds", _g_get_kioskGraceSeconds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "channelOverride", _g_get_channelOverride);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "parkId", _s_set_parkId);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "allowHosting", _s_set_allowHosting);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "listenBaseSeconds", _s_set_listenBaseSeconds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "beaconStaleSeconds", _s_set_beaconStaleSeconds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "reelectionStepSeconds", _s_set_reelectionStepSeconds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "joinTimeoutSeconds", _s_set_joinTimeoutSeconds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "kioskGraceSeconds", _s_set_kioskGraceSeconds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "channelOverride", _s_set_channelOverride);
            
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 2, 2, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "SetParkContext", _m_SetParkContext_xlua_st_);
            
			
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "ArbiterActive", _g_get_ArbiterActive);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Instance", _g_get_Instance);
            
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 1)
				{
					
					var gen_ret = new DreamPark.NetSessionArbiter();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.NetSessionArbiter constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetParkContext_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _newParkId = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.NetSessionArbiter.SetParkContext( _newParkId );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_State(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.State);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_HostId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.HostId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_CurrentHostId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.CurrentHostId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_HostAdvertisedCap(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.HostAdvertisedCap);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsHost(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.IsHost);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_HostedPeerCount(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.HostedPeerCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_ArbiterActive(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, DreamPark.NetSessionArbiter.ArbiterActive);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Instance(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.Push(L, DreamPark.NetSessionArbiter.Instance);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_CanHost(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.CanHost);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Channel(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.Channel);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_parkId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.parkId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_allowHosting(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.allowHosting);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_listenBaseSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.listenBaseSeconds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_beaconStaleSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.beaconStaleSeconds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_reelectionStepSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.reelectionStepSeconds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_joinTimeoutSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.joinTimeoutSeconds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_kioskGraceSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushnumber(L, gen_to_be_invoked.kioskGraceSeconds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_channelOverride(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.channelOverride);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_parkId(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.parkId = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_allowHosting(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.allowHosting = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_listenBaseSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.listenBaseSeconds = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_beaconStaleSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.beaconStaleSeconds = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_reelectionStepSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.reelectionStepSeconds = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_joinTimeoutSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.joinTimeoutSeconds = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_kioskGraceSeconds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.kioskGraceSeconds = (float)LuaAPI.lua_tonumber(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_channelOverride(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.channelOverride = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnStateChanged(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
			DreamPark.NetSessionArbiter gen_to_be_invoked = (DreamPark.NetSessionArbiter)translator.FastGetCSObj(L, 1);
                System.Action<DreamPark.NetSessionArbiter.SessionState> gen_delegate = translator.GetDelegate<System.Action<DreamPark.NetSessionArbiter.SessionState>>(L, 3);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#3 need System.Action<DreamPark.NetSessionArbiter.SessionState>!");
                }
				
				if (gen_param_count == 3)
				{
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "+")) {
						gen_to_be_invoked.OnStateChanged += gen_delegate;
						return 0;
					} 
					
					
					if (LuaAPI.xlua_is_eq_str(L, 2, "-")) {
						gen_to_be_invoked.OnStateChanged -= gen_delegate;
						return 0;
					} 
					
				}
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			LuaAPI.luaL_error(L, "invalid arguments to DreamPark.NetSessionArbiter.OnStateChanged!");
            return 0;
        }
        
		
		
    }
}
