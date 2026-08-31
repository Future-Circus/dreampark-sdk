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
    
    public class UnityEngineAnimatorCullingModeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.AnimatorCullingMode), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.AnimatorCullingMode), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.AnimatorCullingMode), L, null, 5, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "AlwaysAnimate", UnityEngine.AnimatorCullingMode.AlwaysAnimate);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "CullUpdateTransforms", UnityEngine.AnimatorCullingMode.CullUpdateTransforms);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "CullCompletely", UnityEngine.AnimatorCullingMode.CullCompletely);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.AnimatorCullingMode), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineAnimatorCullingMode(L, (UnityEngine.AnimatorCullingMode)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "AlwaysAnimate"))
                {
                    translator.PushUnityEngineAnimatorCullingMode(L, UnityEngine.AnimatorCullingMode.AlwaysAnimate);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "CullUpdateTransforms"))
                {
                    translator.PushUnityEngineAnimatorCullingMode(L, UnityEngine.AnimatorCullingMode.CullUpdateTransforms);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "CullCompletely"))
                {
                    translator.PushUnityEngineAnimatorCullingMode(L, UnityEngine.AnimatorCullingMode.CullCompletely);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.AnimatorCullingMode!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.AnimatorCullingMode! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineSpaceWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.Space), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.Space), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.Space), L, null, 3, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "World", UnityEngine.Space.World);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Self", UnityEngine.Space.Self);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.Space), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineSpace(L, (UnityEngine.Space)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "World"))
                {
                    translator.PushUnityEngineSpace(L, UnityEngine.Space.World);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Self"))
                {
                    translator.PushUnityEngineSpace(L, UnityEngine.Space.Self);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.Space!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.Space! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineForceModeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.ForceMode), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.ForceMode), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.ForceMode), L, null, 5, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Force", UnityEngine.ForceMode.Force);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Acceleration", UnityEngine.ForceMode.Acceleration);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Impulse", UnityEngine.ForceMode.Impulse);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "VelocityChange", UnityEngine.ForceMode.VelocityChange);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.ForceMode), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineForceMode(L, (UnityEngine.ForceMode)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Force"))
                {
                    translator.PushUnityEngineForceMode(L, UnityEngine.ForceMode.Force);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Acceleration"))
                {
                    translator.PushUnityEngineForceMode(L, UnityEngine.ForceMode.Acceleration);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Impulse"))
                {
                    translator.PushUnityEngineForceMode(L, UnityEngine.ForceMode.Impulse);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "VelocityChange"))
                {
                    translator.PushUnityEngineForceMode(L, UnityEngine.ForceMode.VelocityChange);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.ForceMode!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.ForceMode! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEnginePrimitiveTypeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.PrimitiveType), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.PrimitiveType), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.PrimitiveType), L, null, 7, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Sphere", UnityEngine.PrimitiveType.Sphere);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Capsule", UnityEngine.PrimitiveType.Capsule);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Cylinder", UnityEngine.PrimitiveType.Cylinder);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Cube", UnityEngine.PrimitiveType.Cube);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Plane", UnityEngine.PrimitiveType.Plane);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Quad", UnityEngine.PrimitiveType.Quad);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.PrimitiveType), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEnginePrimitiveType(L, (UnityEngine.PrimitiveType)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Sphere"))
                {
                    translator.PushUnityEnginePrimitiveType(L, UnityEngine.PrimitiveType.Sphere);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Capsule"))
                {
                    translator.PushUnityEnginePrimitiveType(L, UnityEngine.PrimitiveType.Capsule);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Cylinder"))
                {
                    translator.PushUnityEnginePrimitiveType(L, UnityEngine.PrimitiveType.Cylinder);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Cube"))
                {
                    translator.PushUnityEnginePrimitiveType(L, UnityEngine.PrimitiveType.Cube);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Plane"))
                {
                    translator.PushUnityEnginePrimitiveType(L, UnityEngine.PrimitiveType.Plane);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Quad"))
                {
                    translator.PushUnityEnginePrimitiveType(L, UnityEngine.PrimitiveType.Quad);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.PrimitiveType!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.PrimitiveType! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineQueryTriggerInteractionWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.QueryTriggerInteraction), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.QueryTriggerInteraction), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.QueryTriggerInteraction), L, null, 4, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "UseGlobal", UnityEngine.QueryTriggerInteraction.UseGlobal);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Ignore", UnityEngine.QueryTriggerInteraction.Ignore);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Collide", UnityEngine.QueryTriggerInteraction.Collide);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.QueryTriggerInteraction), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineQueryTriggerInteraction(L, (UnityEngine.QueryTriggerInteraction)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "UseGlobal"))
                {
                    translator.PushUnityEngineQueryTriggerInteraction(L, UnityEngine.QueryTriggerInteraction.UseGlobal);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Ignore"))
                {
                    translator.PushUnityEngineQueryTriggerInteraction(L, UnityEngine.QueryTriggerInteraction.Ignore);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Collide"))
                {
                    translator.PushUnityEngineQueryTriggerInteraction(L, UnityEngine.QueryTriggerInteraction.Collide);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.QueryTriggerInteraction!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.QueryTriggerInteraction! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineCollisionDetectionModeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.CollisionDetectionMode), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.CollisionDetectionMode), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.CollisionDetectionMode), L, null, 5, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Discrete", UnityEngine.CollisionDetectionMode.Discrete);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Continuous", UnityEngine.CollisionDetectionMode.Continuous);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "ContinuousDynamic", UnityEngine.CollisionDetectionMode.ContinuousDynamic);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "ContinuousSpeculative", UnityEngine.CollisionDetectionMode.ContinuousSpeculative);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.CollisionDetectionMode), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineCollisionDetectionMode(L, (UnityEngine.CollisionDetectionMode)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Discrete"))
                {
                    translator.PushUnityEngineCollisionDetectionMode(L, UnityEngine.CollisionDetectionMode.Discrete);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Continuous"))
                {
                    translator.PushUnityEngineCollisionDetectionMode(L, UnityEngine.CollisionDetectionMode.Continuous);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "ContinuousDynamic"))
                {
                    translator.PushUnityEngineCollisionDetectionMode(L, UnityEngine.CollisionDetectionMode.ContinuousDynamic);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "ContinuousSpeculative"))
                {
                    translator.PushUnityEngineCollisionDetectionMode(L, UnityEngine.CollisionDetectionMode.ContinuousSpeculative);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.CollisionDetectionMode!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.CollisionDetectionMode! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineRigidbodyConstraintsWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.RigidbodyConstraints), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.RigidbodyConstraints), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.RigidbodyConstraints), L, null, 11, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "None", UnityEngine.RigidbodyConstraints.None);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezePositionX", UnityEngine.RigidbodyConstraints.FreezePositionX);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezePositionY", UnityEngine.RigidbodyConstraints.FreezePositionY);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezePositionZ", UnityEngine.RigidbodyConstraints.FreezePositionZ);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezeRotationX", UnityEngine.RigidbodyConstraints.FreezeRotationX);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezeRotationY", UnityEngine.RigidbodyConstraints.FreezeRotationY);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezeRotationZ", UnityEngine.RigidbodyConstraints.FreezeRotationZ);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezePosition", UnityEngine.RigidbodyConstraints.FreezePosition);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezeRotation", UnityEngine.RigidbodyConstraints.FreezeRotation);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "FreezeAll", UnityEngine.RigidbodyConstraints.FreezeAll);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.RigidbodyConstraints), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineRigidbodyConstraints(L, (UnityEngine.RigidbodyConstraints)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "None"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.None);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezePositionX"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezePositionX);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezePositionY"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezePositionY);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezePositionZ"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezePositionZ);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezeRotationX"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezeRotationX);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezeRotationY"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezeRotationY);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezeRotationZ"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezeRotationZ);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezePosition"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezePosition);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezeRotation"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezeRotation);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "FreezeAll"))
                {
                    translator.PushUnityEngineRigidbodyConstraints(L, UnityEngine.RigidbodyConstraints.FreezeAll);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.RigidbodyConstraints!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.RigidbodyConstraints! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineRigidbodyInterpolationWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.RigidbodyInterpolation), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.RigidbodyInterpolation), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.RigidbodyInterpolation), L, null, 4, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "None", UnityEngine.RigidbodyInterpolation.None);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Interpolate", UnityEngine.RigidbodyInterpolation.Interpolate);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Extrapolate", UnityEngine.RigidbodyInterpolation.Extrapolate);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.RigidbodyInterpolation), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineRigidbodyInterpolation(L, (UnityEngine.RigidbodyInterpolation)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "None"))
                {
                    translator.PushUnityEngineRigidbodyInterpolation(L, UnityEngine.RigidbodyInterpolation.None);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Interpolate"))
                {
                    translator.PushUnityEngineRigidbodyInterpolation(L, UnityEngine.RigidbodyInterpolation.Interpolate);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Extrapolate"))
                {
                    translator.PushUnityEngineRigidbodyInterpolation(L, UnityEngine.RigidbodyInterpolation.Extrapolate);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.RigidbodyInterpolation!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.RigidbodyInterpolation! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineLightTypeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.LightType), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.LightType), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.LightType), L, null, 10, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Spot", UnityEngine.LightType.Spot);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Directional", UnityEngine.LightType.Directional);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Point", UnityEngine.LightType.Point);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Rectangle", UnityEngine.LightType.Rectangle);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Disc", UnityEngine.LightType.Disc);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Pyramid", UnityEngine.LightType.Pyramid);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Box", UnityEngine.LightType.Box);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Tube", UnityEngine.LightType.Tube);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.LightType), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineLightType(L, (UnityEngine.LightType)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Spot"))
                {
                    translator.PushUnityEngineLightType(L, UnityEngine.LightType.Spot);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Directional"))
                {
                    translator.PushUnityEngineLightType(L, UnityEngine.LightType.Directional);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Point"))
                {
                    translator.PushUnityEngineLightType(L, UnityEngine.LightType.Point);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Rectangle"))
                {
                    translator.PushUnityEngineLightType(L, UnityEngine.LightType.Rectangle);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Disc"))
                {
                    translator.PushUnityEngineLightType(L, UnityEngine.LightType.Disc);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Pyramid"))
                {
                    translator.PushUnityEngineLightType(L, UnityEngine.LightType.Pyramid);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Box"))
                {
                    translator.PushUnityEngineLightType(L, UnityEngine.LightType.Box);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Tube"))
                {
                    translator.PushUnityEngineLightType(L, UnityEngine.LightType.Tube);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.LightType!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.LightType! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineLightShadowsWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.LightShadows), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.LightShadows), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.LightShadows), L, null, 4, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "None", UnityEngine.LightShadows.None);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Hard", UnityEngine.LightShadows.Hard);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Soft", UnityEngine.LightShadows.Soft);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.LightShadows), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineLightShadows(L, (UnityEngine.LightShadows)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "None"))
                {
                    translator.PushUnityEngineLightShadows(L, UnityEngine.LightShadows.None);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Hard"))
                {
                    translator.PushUnityEngineLightShadows(L, UnityEngine.LightShadows.Hard);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Soft"))
                {
                    translator.PushUnityEngineLightShadows(L, UnityEngine.LightShadows.Soft);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.LightShadows!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.LightShadows! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineCameraClearFlagsWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.CameraClearFlags), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.CameraClearFlags), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.CameraClearFlags), L, null, 6, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Skybox", UnityEngine.CameraClearFlags.Skybox);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Color", UnityEngine.CameraClearFlags.Color);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "SolidColor", UnityEngine.CameraClearFlags.SolidColor);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Depth", UnityEngine.CameraClearFlags.Depth);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Nothing", UnityEngine.CameraClearFlags.Nothing);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.CameraClearFlags), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineCameraClearFlags(L, (UnityEngine.CameraClearFlags)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Skybox"))
                {
                    translator.PushUnityEngineCameraClearFlags(L, UnityEngine.CameraClearFlags.Skybox);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Color"))
                {
                    translator.PushUnityEngineCameraClearFlags(L, UnityEngine.CameraClearFlags.Color);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "SolidColor"))
                {
                    translator.PushUnityEngineCameraClearFlags(L, UnityEngine.CameraClearFlags.SolidColor);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Depth"))
                {
                    translator.PushUnityEngineCameraClearFlags(L, UnityEngine.CameraClearFlags.Depth);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Nothing"))
                {
                    translator.PushUnityEngineCameraClearFlags(L, UnityEngine.CameraClearFlags.Nothing);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.CameraClearFlags!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.CameraClearFlags! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineTextureWrapModeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.TextureWrapMode), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.TextureWrapMode), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.TextureWrapMode), L, null, 5, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Repeat", UnityEngine.TextureWrapMode.Repeat);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Clamp", UnityEngine.TextureWrapMode.Clamp);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Mirror", UnityEngine.TextureWrapMode.Mirror);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MirrorOnce", UnityEngine.TextureWrapMode.MirrorOnce);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.TextureWrapMode), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineTextureWrapMode(L, (UnityEngine.TextureWrapMode)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Repeat"))
                {
                    translator.PushUnityEngineTextureWrapMode(L, UnityEngine.TextureWrapMode.Repeat);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Clamp"))
                {
                    translator.PushUnityEngineTextureWrapMode(L, UnityEngine.TextureWrapMode.Clamp);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Mirror"))
                {
                    translator.PushUnityEngineTextureWrapMode(L, UnityEngine.TextureWrapMode.Mirror);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "MirrorOnce"))
                {
                    translator.PushUnityEngineTextureWrapMode(L, UnityEngine.TextureWrapMode.MirrorOnce);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.TextureWrapMode!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.TextureWrapMode! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineFilterModeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.FilterMode), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.FilterMode), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.FilterMode), L, null, 4, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Point", UnityEngine.FilterMode.Point);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Bilinear", UnityEngine.FilterMode.Bilinear);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Trilinear", UnityEngine.FilterMode.Trilinear);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.FilterMode), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineFilterMode(L, (UnityEngine.FilterMode)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Point"))
                {
                    translator.PushUnityEngineFilterMode(L, UnityEngine.FilterMode.Point);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Bilinear"))
                {
                    translator.PushUnityEngineFilterMode(L, UnityEngine.FilterMode.Bilinear);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Trilinear"))
                {
                    translator.PushUnityEngineFilterMode(L, UnityEngine.FilterMode.Trilinear);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.FilterMode!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.FilterMode! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineRenderModeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.RenderMode), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.RenderMode), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.RenderMode), L, null, 4, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "ScreenSpaceOverlay", UnityEngine.RenderMode.ScreenSpaceOverlay);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "ScreenSpaceCamera", UnityEngine.RenderMode.ScreenSpaceCamera);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "WorldSpace", UnityEngine.RenderMode.WorldSpace);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.RenderMode), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineRenderMode(L, (UnityEngine.RenderMode)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "ScreenSpaceOverlay"))
                {
                    translator.PushUnityEngineRenderMode(L, UnityEngine.RenderMode.ScreenSpaceOverlay);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "ScreenSpaceCamera"))
                {
                    translator.PushUnityEngineRenderMode(L, UnityEngine.RenderMode.ScreenSpaceCamera);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "WorldSpace"))
                {
                    translator.PushUnityEngineRenderMode(L, UnityEngine.RenderMode.WorldSpace);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.RenderMode!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.RenderMode! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineSendMessageOptionsWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.SendMessageOptions), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.SendMessageOptions), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.SendMessageOptions), L, null, 3, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "RequireReceiver", UnityEngine.SendMessageOptions.RequireReceiver);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "DontRequireReceiver", UnityEngine.SendMessageOptions.DontRequireReceiver);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.SendMessageOptions), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineSendMessageOptions(L, (UnityEngine.SendMessageOptions)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "RequireReceiver"))
                {
                    translator.PushUnityEngineSendMessageOptions(L, UnityEngine.SendMessageOptions.RequireReceiver);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "DontRequireReceiver"))
                {
                    translator.PushUnityEngineSendMessageOptions(L, UnityEngine.SendMessageOptions.DontRequireReceiver);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.SendMessageOptions!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.SendMessageOptions! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineFindObjectsSortModeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.FindObjectsSortMode), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.FindObjectsSortMode), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.FindObjectsSortMode), L, null, 3, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "None", UnityEngine.FindObjectsSortMode.None);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "InstanceID", UnityEngine.FindObjectsSortMode.InstanceID);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.FindObjectsSortMode), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineFindObjectsSortMode(L, (UnityEngine.FindObjectsSortMode)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "None"))
                {
                    translator.PushUnityEngineFindObjectsSortMode(L, UnityEngine.FindObjectsSortMode.None);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "InstanceID"))
                {
                    translator.PushUnityEngineFindObjectsSortMode(L, UnityEngine.FindObjectsSortMode.InstanceID);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.FindObjectsSortMode!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.FindObjectsSortMode! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineFindObjectsInactiveWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.FindObjectsInactive), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.FindObjectsInactive), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.FindObjectsInactive), L, null, 3, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Exclude", UnityEngine.FindObjectsInactive.Exclude);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Include", UnityEngine.FindObjectsInactive.Include);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.FindObjectsInactive), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineFindObjectsInactive(L, (UnityEngine.FindObjectsInactive)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Exclude"))
                {
                    translator.PushUnityEngineFindObjectsInactive(L, UnityEngine.FindObjectsInactive.Exclude);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Include"))
                {
                    translator.PushUnityEngineFindObjectsInactive(L, UnityEngine.FindObjectsInactive.Include);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.FindObjectsInactive!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.FindObjectsInactive! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineHideFlagsWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.HideFlags), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.HideFlags), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.HideFlags), L, null, 10, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "None", UnityEngine.HideFlags.None);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "HideInHierarchy", UnityEngine.HideFlags.HideInHierarchy);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "HideInInspector", UnityEngine.HideFlags.HideInInspector);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "DontSaveInEditor", UnityEngine.HideFlags.DontSaveInEditor);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "NotEditable", UnityEngine.HideFlags.NotEditable);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "DontSaveInBuild", UnityEngine.HideFlags.DontSaveInBuild);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "DontUnloadUnusedAsset", UnityEngine.HideFlags.DontUnloadUnusedAsset);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "DontSave", UnityEngine.HideFlags.DontSave);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "HideAndDontSave", UnityEngine.HideFlags.HideAndDontSave);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.HideFlags), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineHideFlags(L, (UnityEngine.HideFlags)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "None"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.None);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "HideInHierarchy"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.HideInHierarchy);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "HideInInspector"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.HideInInspector);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "DontSaveInEditor"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.DontSaveInEditor);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "NotEditable"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.NotEditable);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "DontSaveInBuild"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.DontSaveInBuild);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "DontUnloadUnusedAsset"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.DontUnloadUnusedAsset);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "DontSave"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.DontSave);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "HideAndDontSave"))
                {
                    translator.PushUnityEngineHideFlags(L, UnityEngine.HideFlags.HideAndDontSave);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.HideFlags!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.HideFlags! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineRuntimePlatformWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.RuntimePlatform), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.RuntimePlatform), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.RuntimePlatform), L, null, 56, 0, 0);

            Utils.RegisterEnumType(L, typeof(UnityEngine.RuntimePlatform));

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.RuntimePlatform), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineRuntimePlatform(L, (UnityEngine.RuntimePlatform)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

                try
				{
                    translator.TranslateToEnumToTop(L, typeof(UnityEngine.RuntimePlatform), 1);
				}
				catch (System.Exception e)
				{
					return LuaAPI.luaL_error(L, "cast to " + typeof(UnityEngine.RuntimePlatform) + " exception:" + e);
				}

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.RuntimePlatform! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineNetworkReachabilityWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.NetworkReachability), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.NetworkReachability), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.NetworkReachability), L, null, 4, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "NotReachable", UnityEngine.NetworkReachability.NotReachable);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "ReachableViaCarrierDataNetwork", UnityEngine.NetworkReachability.ReachableViaCarrierDataNetwork);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "ReachableViaLocalAreaNetwork", UnityEngine.NetworkReachability.ReachableViaLocalAreaNetwork);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.NetworkReachability), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineNetworkReachability(L, (UnityEngine.NetworkReachability)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "NotReachable"))
                {
                    translator.PushUnityEngineNetworkReachability(L, UnityEngine.NetworkReachability.NotReachable);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "ReachableViaCarrierDataNetwork"))
                {
                    translator.PushUnityEngineNetworkReachability(L, UnityEngine.NetworkReachability.ReachableViaCarrierDataNetwork);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "ReachableViaLocalAreaNetwork"))
                {
                    translator.PushUnityEngineNetworkReachability(L, UnityEngine.NetworkReachability.ReachableViaLocalAreaNetwork);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.NetworkReachability!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.NetworkReachability! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineAudioRolloffModeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.AudioRolloffMode), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.AudioRolloffMode), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.AudioRolloffMode), L, null, 4, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Logarithmic", UnityEngine.AudioRolloffMode.Logarithmic);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Linear", UnityEngine.AudioRolloffMode.Linear);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "Custom", UnityEngine.AudioRolloffMode.Custom);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.AudioRolloffMode), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineAudioRolloffMode(L, (UnityEngine.AudioRolloffMode)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "Logarithmic"))
                {
                    translator.PushUnityEngineAudioRolloffMode(L, UnityEngine.AudioRolloffMode.Logarithmic);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Linear"))
                {
                    translator.PushUnityEngineAudioRolloffMode(L, UnityEngine.AudioRolloffMode.Linear);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "Custom"))
                {
                    translator.PushUnityEngineAudioRolloffMode(L, UnityEngine.AudioRolloffMode.Custom);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.AudioRolloffMode!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.AudioRolloffMode! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineParticleSystemStopBehaviorWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.ParticleSystemStopBehavior), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.ParticleSystemStopBehavior), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.ParticleSystemStopBehavior), L, null, 3, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "StopEmittingAndClear", UnityEngine.ParticleSystemStopBehavior.StopEmittingAndClear);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "StopEmitting", UnityEngine.ParticleSystemStopBehavior.StopEmitting);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.ParticleSystemStopBehavior), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineParticleSystemStopBehavior(L, (UnityEngine.ParticleSystemStopBehavior)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "StopEmittingAndClear"))
                {
                    translator.PushUnityEngineParticleSystemStopBehavior(L, UnityEngine.ParticleSystemStopBehavior.StopEmittingAndClear);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "StopEmitting"))
                {
                    translator.PushUnityEngineParticleSystemStopBehavior(L, UnityEngine.ParticleSystemStopBehavior.StopEmitting);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.ParticleSystemStopBehavior!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.ParticleSystemStopBehavior! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineAINavMeshPathStatusWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.AI.NavMeshPathStatus), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.AI.NavMeshPathStatus), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.AI.NavMeshPathStatus), L, null, 4, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "PathComplete", UnityEngine.AI.NavMeshPathStatus.PathComplete);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "PathPartial", UnityEngine.AI.NavMeshPathStatus.PathPartial);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "PathInvalid", UnityEngine.AI.NavMeshPathStatus.PathInvalid);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.AI.NavMeshPathStatus), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineAINavMeshPathStatus(L, (UnityEngine.AI.NavMeshPathStatus)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "PathComplete"))
                {
                    translator.PushUnityEngineAINavMeshPathStatus(L, UnityEngine.AI.NavMeshPathStatus.PathComplete);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "PathPartial"))
                {
                    translator.PushUnityEngineAINavMeshPathStatus(L, UnityEngine.AI.NavMeshPathStatus.PathPartial);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "PathInvalid"))
                {
                    translator.PushUnityEngineAINavMeshPathStatus(L, UnityEngine.AI.NavMeshPathStatus.PathInvalid);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.AI.NavMeshPathStatus!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.AI.NavMeshPathStatus! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class UnityEngineAIObstacleAvoidanceTypeWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(UnityEngine.AI.ObstacleAvoidanceType), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(UnityEngine.AI.ObstacleAvoidanceType), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(UnityEngine.AI.ObstacleAvoidanceType), L, null, 6, 0, 0);

            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "NoObstacleAvoidance", UnityEngine.AI.ObstacleAvoidanceType.NoObstacleAvoidance);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "LowQualityObstacleAvoidance", UnityEngine.AI.ObstacleAvoidanceType.LowQualityObstacleAvoidance);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "MedQualityObstacleAvoidance", UnityEngine.AI.ObstacleAvoidanceType.MedQualityObstacleAvoidance);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "GoodQualityObstacleAvoidance", UnityEngine.AI.ObstacleAvoidanceType.GoodQualityObstacleAvoidance);
            
            Utils.RegisterObject(L, translator, Utils.CLS_IDX, "HighQualityObstacleAvoidance", UnityEngine.AI.ObstacleAvoidanceType.HighQualityObstacleAvoidance);
            

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(UnityEngine.AI.ObstacleAvoidanceType), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushUnityEngineAIObstacleAvoidanceType(L, (UnityEngine.AI.ObstacleAvoidanceType)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

			    if (LuaAPI.xlua_is_eq_str(L, 1, "NoObstacleAvoidance"))
                {
                    translator.PushUnityEngineAIObstacleAvoidanceType(L, UnityEngine.AI.ObstacleAvoidanceType.NoObstacleAvoidance);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "LowQualityObstacleAvoidance"))
                {
                    translator.PushUnityEngineAIObstacleAvoidanceType(L, UnityEngine.AI.ObstacleAvoidanceType.LowQualityObstacleAvoidance);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "MedQualityObstacleAvoidance"))
                {
                    translator.PushUnityEngineAIObstacleAvoidanceType(L, UnityEngine.AI.ObstacleAvoidanceType.MedQualityObstacleAvoidance);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "GoodQualityObstacleAvoidance"))
                {
                    translator.PushUnityEngineAIObstacleAvoidanceType(L, UnityEngine.AI.ObstacleAvoidanceType.GoodQualityObstacleAvoidance);
                }
				else if (LuaAPI.xlua_is_eq_str(L, 1, "HighQualityObstacleAvoidance"))
                {
                    translator.PushUnityEngineAIObstacleAvoidanceType(L, UnityEngine.AI.ObstacleAvoidanceType.HighQualityObstacleAvoidance);
                }
				else
                {
                    return LuaAPI.luaL_error(L, "invalid string for UnityEngine.AI.ObstacleAvoidanceType!");
                }

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for UnityEngine.AI.ObstacleAvoidanceType! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
    public class TMProTextAlignmentOptionsWrap
    {
		public static void __Register(RealStatePtr L)
        {
		    ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
		    Utils.BeginObjectRegister(typeof(TMPro.TextAlignmentOptions), L, translator, 0, 0, 0, 0);
			Utils.EndObjectRegister(typeof(TMPro.TextAlignmentOptions), L, translator, null, null, null, null, null);
			
			Utils.BeginClassRegister(typeof(TMPro.TextAlignmentOptions), L, null, 38, 0, 0);

            Utils.RegisterEnumType(L, typeof(TMPro.TextAlignmentOptions));

			Utils.RegisterFunc(L, Utils.CLS_IDX, "__CastFrom", __CastFrom);
            
            Utils.EndClassRegister(typeof(TMPro.TextAlignmentOptions), L, translator);
        }
		
		[MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CastFrom(RealStatePtr L)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaTypes lua_type = LuaAPI.lua_type(L, 1);
            if (lua_type == LuaTypes.LUA_TNUMBER)
            {
                translator.PushTMProTextAlignmentOptions(L, (TMPro.TextAlignmentOptions)LuaAPI.xlua_tointeger(L, 1));
            }
			
            else if(lua_type == LuaTypes.LUA_TSTRING)
            {

                try
				{
                    translator.TranslateToEnumToTop(L, typeof(TMPro.TextAlignmentOptions), 1);
				}
				catch (System.Exception e)
				{
					return LuaAPI.luaL_error(L, "cast to " + typeof(TMPro.TextAlignmentOptions) + " exception:" + e);
				}

            }
			
            else
            {
                return LuaAPI.luaL_error(L, "invalid lua type for TMPro.TextAlignmentOptions! Expect number or string, got + " + lua_type);
            }

            return 1;
		}
	}
    
}