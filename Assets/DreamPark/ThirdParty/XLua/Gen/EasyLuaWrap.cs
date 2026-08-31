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
    public class EasyLuaWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(EasyLua);
			Utils.BeginObjectRegister(type, L, translator, 0, 4, 17, 17);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "Awake", _m_Awake);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "EnsureBooted", _m_EnsureBooted);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "OnEvent", _m_OnEvent);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "OnEventDisable", _m_OnEventDisable);
			
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "luaScript", _g_get_luaScript);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "injections", _g_get_injections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "floatInjections", _g_get_floatInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "stringInjections", _g_get_stringInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "boolInjections", _g_get_boolInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "intInjections", _g_get_intInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "scriptInjections", _g_get_scriptInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "audioClipInjections", _g_get_audioClipInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "vector3Injections", _g_get_vector3Injections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "colorInjections", _g_get_colorInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "transformInjections", _g_get_transformInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "materialInjections", _g_get_materialInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "spriteInjections", _g_get_spriteInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "textureInjections", _g_get_textureInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "componentInjections", _g_get_componentInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "gameObjectListInjections", _g_get_gameObjectListInjections);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "delayNextEvent", _g_get_delayNextEvent);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "luaScript", _s_set_luaScript);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "injections", _s_set_injections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "floatInjections", _s_set_floatInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "stringInjections", _s_set_stringInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "boolInjections", _s_set_boolInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "intInjections", _s_set_intInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "scriptInjections", _s_set_scriptInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "audioClipInjections", _s_set_audioClipInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "vector3Injections", _s_set_vector3Injections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "colorInjections", _s_set_colorInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "transformInjections", _s_set_transformInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "materialInjections", _s_set_materialInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "spriteInjections", _s_set_spriteInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "textureInjections", _s_set_textureInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "componentInjections", _s_set_componentInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "gameObjectListInjections", _s_set_gameObjectListInjections);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "delayNextEvent", _s_set_delayNextEvent);
            
			
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
					
					var gen_ret = new EasyLua();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to EasyLua constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Awake(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.Awake(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EnsureBooted(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.EnsureBooted(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_OnEvent(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& translator.Assignable<object>(L, 2)) 
                {
                    object _arg0 = translator.GetObject(L, 2, typeof(object));
                    
                    gen_to_be_invoked.OnEvent( _arg0 );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1) 
                {
                    
                    gen_to_be_invoked.OnEvent(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to EasyLua.OnEvent!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_OnEventDisable(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.OnEventDisable(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_luaScript(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.luaScript);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_injections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.injections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_floatInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.floatInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_stringInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.stringInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_boolInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.boolInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_intInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.intInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_scriptInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.scriptInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_audioClipInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.audioClipInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_vector3Injections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.vector3Injections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_colorInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.colorInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_transformInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.transformInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_materialInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.materialInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_spriteInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.spriteInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_textureInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.textureInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_componentInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.componentInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_gameObjectListInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.gameObjectListInjections);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_delayNextEvent(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.delayNextEvent);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_luaScript(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.luaScript = (UnityEngine.TextAsset)translator.GetObject(L, 2, typeof(UnityEngine.TextAsset));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_injections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.injections = (Injection[])translator.GetObject(L, 2, typeof(Injection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_floatInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.floatInjections = (FloatInjection[])translator.GetObject(L, 2, typeof(FloatInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_stringInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.stringInjections = (StringInjection[])translator.GetObject(L, 2, typeof(StringInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_boolInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.boolInjections = (BoolInjection[])translator.GetObject(L, 2, typeof(BoolInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_intInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.intInjections = (IntInjection[])translator.GetObject(L, 2, typeof(IntInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_scriptInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.scriptInjections = (ScriptInjection[])translator.GetObject(L, 2, typeof(ScriptInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_audioClipInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.audioClipInjections = (AudioClipInjection[])translator.GetObject(L, 2, typeof(AudioClipInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_vector3Injections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.vector3Injections = (Vector3Injection[])translator.GetObject(L, 2, typeof(Vector3Injection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_colorInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.colorInjections = (ColorInjection[])translator.GetObject(L, 2, typeof(ColorInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_transformInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.transformInjections = (TransformInjection[])translator.GetObject(L, 2, typeof(TransformInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_materialInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.materialInjections = (MaterialInjection[])translator.GetObject(L, 2, typeof(MaterialInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_spriteInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.spriteInjections = (SpriteInjection[])translator.GetObject(L, 2, typeof(SpriteInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_textureInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.textureInjections = (TextureInjection[])translator.GetObject(L, 2, typeof(TextureInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_componentInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.componentInjections = (ComponentInjection[])translator.GetObject(L, 2, typeof(ComponentInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_gameObjectListInjections(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.gameObjectListInjections = (GameObjectListInjection[])translator.GetObject(L, 2, typeof(GameObjectListInjection[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_delayNextEvent(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                EasyLua gen_to_be_invoked = (EasyLua)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.delayNextEvent = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
		
		
    }
}
