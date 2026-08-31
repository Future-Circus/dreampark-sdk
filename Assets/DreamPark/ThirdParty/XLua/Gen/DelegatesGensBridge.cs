#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

using System;


namespace XLua
{
    public partial class DelegateBridge : DelegateBridgeBase
    {
		
		public void __Gen_Delegate_Imp0()
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                
                
                PCall(L, 0, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp1(bool p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                
                LuaAPI.lua_pushboolean(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp2(int p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                
                LuaAPI.xlua_pushinteger(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp3(float p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                
                LuaAPI.lua_pushnumber(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp4(string p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                
                LuaAPI.lua_pushstring(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp5(UnityEngine.GameObject p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                ObjectTranslator translator = luaEnv.translator;
                translator.Push(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp6(UnityEngine.Transform p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                ObjectTranslator translator = luaEnv.translator;
                translator.Push(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp7(UnityEngine.Collider p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                ObjectTranslator translator = luaEnv.translator;
                translator.Push(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp8(UnityEngine.Collision p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                ObjectTranslator translator = luaEnv.translator;
                translator.Push(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public void __Gen_Delegate_Imp9(UnityEngine.Vector3 p0)
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                ObjectTranslator translator = luaEnv.translator;
                translator.PushUnityEngineVector3(L, p0);
                
                PCall(L, 1, 0, errFunc);
                
                
                
                LuaAPI.lua_settop(L, errFunc - 1);
                
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
		public bool __Gen_Delegate_Imp10()
		{
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnv.luaEnvLock)
            {
#endif
                RealStatePtr L = luaEnv.rawL;
                int errFunc = LuaAPI.pcall_prepare(L, errorFuncRef, luaReference);
                
                
                PCall(L, 0, 1, errFunc);
                
                
                bool __gen_ret = LuaAPI.lua_toboolean(L, errFunc + 1);
                LuaAPI.lua_settop(L, errFunc - 1);
                return  __gen_ret;
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
		}
        
        
		static DelegateBridge()
		{
		    Gen_Flag = true;
		}
		
		public override Delegate GetDelegateByType(Type type)
		{
		
		    if (type == typeof(System.Action))
			{
			    return new System.Action(__Gen_Delegate_Imp0);
			}
		
		    if (type == typeof(UnityEngine.Events.UnityAction))
			{
			    return new UnityEngine.Events.UnityAction(__Gen_Delegate_Imp0);
			}
		
		    if (type == typeof(System.Action<bool>))
			{
			    return new System.Action<bool>(__Gen_Delegate_Imp1);
			}
		
		    if (type == typeof(UnityEngine.Events.UnityAction<bool>))
			{
			    return new UnityEngine.Events.UnityAction<bool>(__Gen_Delegate_Imp1);
			}
		
		    if (type == typeof(System.Action<int>))
			{
			    return new System.Action<int>(__Gen_Delegate_Imp2);
			}
		
		    if (type == typeof(System.Action<float>))
			{
			    return new System.Action<float>(__Gen_Delegate_Imp3);
			}
		
		    if (type == typeof(UnityEngine.Events.UnityAction<float>))
			{
			    return new UnityEngine.Events.UnityAction<float>(__Gen_Delegate_Imp3);
			}
		
		    if (type == typeof(System.Action<string>))
			{
			    return new System.Action<string>(__Gen_Delegate_Imp4);
			}
		
		    if (type == typeof(System.Action<UnityEngine.GameObject>))
			{
			    return new System.Action<UnityEngine.GameObject>(__Gen_Delegate_Imp5);
			}
		
		    if (type == typeof(System.Action<UnityEngine.Transform>))
			{
			    return new System.Action<UnityEngine.Transform>(__Gen_Delegate_Imp6);
			}
		
		    if (type == typeof(System.Action<UnityEngine.Collider>))
			{
			    return new System.Action<UnityEngine.Collider>(__Gen_Delegate_Imp7);
			}
		
		    if (type == typeof(System.Action<UnityEngine.Collision>))
			{
			    return new System.Action<UnityEngine.Collision>(__Gen_Delegate_Imp8);
			}
		
		    if (type == typeof(System.Action<UnityEngine.Vector3>))
			{
			    return new System.Action<UnityEngine.Vector3>(__Gen_Delegate_Imp9);
			}
		
		    if (type == typeof(System.Func<bool>))
			{
			    return new System.Func<bool>(__Gen_Delegate_Imp10);
			}
		
		    return null;
		}
	}
    
}