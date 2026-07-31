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
    public partial class ObjectTranslator
    {
        
        class IniterAdderUnityEngineColor32
        {
            static IniterAdderUnityEngineColor32()
            {
                LuaEnv.AddIniter(Init);
            }
			
			static void Init(LuaEnv luaenv, ObjectTranslator translator)
			{
			
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Color32>(translator.PushUnityEngineColor32, translator.Get, translator.UpdateUnityEngineColor32);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Rect>(translator.PushUnityEngineRect, translator.Get, translator.UpdateUnityEngineRect);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Keyframe>(translator.PushUnityEngineKeyframe, translator.Get, translator.UpdateUnityEngineKeyframe);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Vector2>(translator.PushUnityEngineVector2, translator.Get, translator.UpdateUnityEngineVector2);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Vector3>(translator.PushUnityEngineVector3, translator.Get, translator.UpdateUnityEngineVector3);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Vector4>(translator.PushUnityEngineVector4, translator.Get, translator.UpdateUnityEngineVector4);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Color>(translator.PushUnityEngineColor, translator.Get, translator.UpdateUnityEngineColor);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Quaternion>(translator.PushUnityEngineQuaternion, translator.Get, translator.UpdateUnityEngineQuaternion);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Ray>(translator.PushUnityEngineRay, translator.Get, translator.UpdateUnityEngineRay);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Bounds>(translator.PushUnityEngineBounds, translator.Get, translator.UpdateUnityEngineBounds);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Ray2D>(translator.PushUnityEngineRay2D, translator.Get, translator.UpdateUnityEngineRay2D);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.AnimatorCullingMode>(translator.PushUnityEngineAnimatorCullingMode, translator.Get, translator.UpdateUnityEngineAnimatorCullingMode);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.Space>(translator.PushUnityEngineSpace, translator.Get, translator.UpdateUnityEngineSpace);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.ForceMode>(translator.PushUnityEngineForceMode, translator.Get, translator.UpdateUnityEngineForceMode);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.PrimitiveType>(translator.PushUnityEnginePrimitiveType, translator.Get, translator.UpdateUnityEnginePrimitiveType);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.QueryTriggerInteraction>(translator.PushUnityEngineQueryTriggerInteraction, translator.Get, translator.UpdateUnityEngineQueryTriggerInteraction);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.CollisionDetectionMode>(translator.PushUnityEngineCollisionDetectionMode, translator.Get, translator.UpdateUnityEngineCollisionDetectionMode);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.RigidbodyConstraints>(translator.PushUnityEngineRigidbodyConstraints, translator.Get, translator.UpdateUnityEngineRigidbodyConstraints);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.RigidbodyInterpolation>(translator.PushUnityEngineRigidbodyInterpolation, translator.Get, translator.UpdateUnityEngineRigidbodyInterpolation);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.LightType>(translator.PushUnityEngineLightType, translator.Get, translator.UpdateUnityEngineLightType);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.LightShadows>(translator.PushUnityEngineLightShadows, translator.Get, translator.UpdateUnityEngineLightShadows);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.CameraClearFlags>(translator.PushUnityEngineCameraClearFlags, translator.Get, translator.UpdateUnityEngineCameraClearFlags);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.TextureWrapMode>(translator.PushUnityEngineTextureWrapMode, translator.Get, translator.UpdateUnityEngineTextureWrapMode);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.FilterMode>(translator.PushUnityEngineFilterMode, translator.Get, translator.UpdateUnityEngineFilterMode);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.RenderMode>(translator.PushUnityEngineRenderMode, translator.Get, translator.UpdateUnityEngineRenderMode);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.SendMessageOptions>(translator.PushUnityEngineSendMessageOptions, translator.Get, translator.UpdateUnityEngineSendMessageOptions);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.FindObjectsSortMode>(translator.PushUnityEngineFindObjectsSortMode, translator.Get, translator.UpdateUnityEngineFindObjectsSortMode);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.FindObjectsInactive>(translator.PushUnityEngineFindObjectsInactive, translator.Get, translator.UpdateUnityEngineFindObjectsInactive);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.HideFlags>(translator.PushUnityEngineHideFlags, translator.Get, translator.UpdateUnityEngineHideFlags);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.RuntimePlatform>(translator.PushUnityEngineRuntimePlatform, translator.Get, translator.UpdateUnityEngineRuntimePlatform);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.NetworkReachability>(translator.PushUnityEngineNetworkReachability, translator.Get, translator.UpdateUnityEngineNetworkReachability);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.AudioRolloffMode>(translator.PushUnityEngineAudioRolloffMode, translator.Get, translator.UpdateUnityEngineAudioRolloffMode);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.ParticleSystemStopBehavior>(translator.PushUnityEngineParticleSystemStopBehavior, translator.Get, translator.UpdateUnityEngineParticleSystemStopBehavior);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.AI.NavMeshPathStatus>(translator.PushUnityEngineAINavMeshPathStatus, translator.Get, translator.UpdateUnityEngineAINavMeshPathStatus);
				translator.RegisterPushAndGetAndUpdate<UnityEngine.AI.ObstacleAvoidanceType>(translator.PushUnityEngineAIObstacleAvoidanceType, translator.Get, translator.UpdateUnityEngineAIObstacleAvoidanceType);
				translator.RegisterPushAndGetAndUpdate<TMPro.TextAlignmentOptions>(translator.PushTMProTextAlignmentOptions, translator.Get, translator.UpdateTMProTextAlignmentOptions);
			
			}
        }
        
        static IniterAdderUnityEngineColor32 s_IniterAdderUnityEngineColor32_dumb_obj = new IniterAdderUnityEngineColor32();
        static IniterAdderUnityEngineColor32 IniterAdderUnityEngineColor32_dumb_obj {get{return s_IniterAdderUnityEngineColor32_dumb_obj;}}
        
        
        int UnityEngineColor32_TypeID = -1;
        public void PushUnityEngineColor32(RealStatePtr L, UnityEngine.Color32 val)
        {
            if (UnityEngineColor32_TypeID == -1)
            {
			    bool is_first;
                UnityEngineColor32_TypeID = getTypeId(L, typeof(UnityEngine.Color32), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineColor32_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Color32 ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Color32 val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineColor32_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Color32");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Color32");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Color32)objectCasters.GetCaster(typeof(UnityEngine.Color32))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineColor32(RealStatePtr L, int index, UnityEngine.Color32 val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineColor32_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Color32");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Color32 ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineRect_TypeID = -1;
        public void PushUnityEngineRect(RealStatePtr L, UnityEngine.Rect val)
        {
            if (UnityEngineRect_TypeID == -1)
            {
			    bool is_first;
                UnityEngineRect_TypeID = getTypeId(L, typeof(UnityEngine.Rect), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 0, UnityEngineRect_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Rect ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Rect val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRect_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Rect");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Rect");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Rect)objectCasters.GetCaster(typeof(UnityEngine.Rect))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineRect(RealStatePtr L, int index, UnityEngine.Rect val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRect_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Rect");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Rect ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineKeyframe_TypeID = -1;
        public void PushUnityEngineKeyframe(RealStatePtr L, UnityEngine.Keyframe val)
        {
            if (UnityEngineKeyframe_TypeID == -1)
            {
			    bool is_first;
                UnityEngineKeyframe_TypeID = getTypeId(L, typeof(UnityEngine.Keyframe), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 0, UnityEngineKeyframe_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Keyframe ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Keyframe val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineKeyframe_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Keyframe");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Keyframe");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Keyframe)objectCasters.GetCaster(typeof(UnityEngine.Keyframe))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineKeyframe(RealStatePtr L, int index, UnityEngine.Keyframe val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineKeyframe_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Keyframe");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Keyframe ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineVector2_TypeID = -1;
        public void PushUnityEngineVector2(RealStatePtr L, UnityEngine.Vector2 val)
        {
            if (UnityEngineVector2_TypeID == -1)
            {
			    bool is_first;
                UnityEngineVector2_TypeID = getTypeId(L, typeof(UnityEngine.Vector2), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 8, UnityEngineVector2_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Vector2 ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Vector2 val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineVector2_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Vector2");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Vector2");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Vector2)objectCasters.GetCaster(typeof(UnityEngine.Vector2))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineVector2(RealStatePtr L, int index, UnityEngine.Vector2 val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineVector2_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Vector2");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Vector2 ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineVector3_TypeID = -1;
        public void PushUnityEngineVector3(RealStatePtr L, UnityEngine.Vector3 val)
        {
            if (UnityEngineVector3_TypeID == -1)
            {
			    bool is_first;
                UnityEngineVector3_TypeID = getTypeId(L, typeof(UnityEngine.Vector3), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 12, UnityEngineVector3_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Vector3 ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Vector3 val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineVector3_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Vector3");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Vector3");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Vector3)objectCasters.GetCaster(typeof(UnityEngine.Vector3))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineVector3(RealStatePtr L, int index, UnityEngine.Vector3 val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineVector3_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Vector3");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Vector3 ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineVector4_TypeID = -1;
        public void PushUnityEngineVector4(RealStatePtr L, UnityEngine.Vector4 val)
        {
            if (UnityEngineVector4_TypeID == -1)
            {
			    bool is_first;
                UnityEngineVector4_TypeID = getTypeId(L, typeof(UnityEngine.Vector4), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 16, UnityEngineVector4_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Vector4 ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Vector4 val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineVector4_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Vector4");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Vector4");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Vector4)objectCasters.GetCaster(typeof(UnityEngine.Vector4))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineVector4(RealStatePtr L, int index, UnityEngine.Vector4 val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineVector4_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Vector4");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Vector4 ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineColor_TypeID = -1;
        public void PushUnityEngineColor(RealStatePtr L, UnityEngine.Color val)
        {
            if (UnityEngineColor_TypeID == -1)
            {
			    bool is_first;
                UnityEngineColor_TypeID = getTypeId(L, typeof(UnityEngine.Color), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 16, UnityEngineColor_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Color ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Color val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineColor_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Color");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Color");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Color)objectCasters.GetCaster(typeof(UnityEngine.Color))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineColor(RealStatePtr L, int index, UnityEngine.Color val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineColor_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Color");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Color ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineQuaternion_TypeID = -1;
        public void PushUnityEngineQuaternion(RealStatePtr L, UnityEngine.Quaternion val)
        {
            if (UnityEngineQuaternion_TypeID == -1)
            {
			    bool is_first;
                UnityEngineQuaternion_TypeID = getTypeId(L, typeof(UnityEngine.Quaternion), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 16, UnityEngineQuaternion_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Quaternion ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Quaternion val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineQuaternion_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Quaternion");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Quaternion");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Quaternion)objectCasters.GetCaster(typeof(UnityEngine.Quaternion))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineQuaternion(RealStatePtr L, int index, UnityEngine.Quaternion val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineQuaternion_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Quaternion");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Quaternion ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineRay_TypeID = -1;
        public void PushUnityEngineRay(RealStatePtr L, UnityEngine.Ray val)
        {
            if (UnityEngineRay_TypeID == -1)
            {
			    bool is_first;
                UnityEngineRay_TypeID = getTypeId(L, typeof(UnityEngine.Ray), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 24, UnityEngineRay_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Ray ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Ray val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRay_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Ray");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Ray");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Ray)objectCasters.GetCaster(typeof(UnityEngine.Ray))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineRay(RealStatePtr L, int index, UnityEngine.Ray val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRay_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Ray");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Ray ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineBounds_TypeID = -1;
        public void PushUnityEngineBounds(RealStatePtr L, UnityEngine.Bounds val)
        {
            if (UnityEngineBounds_TypeID == -1)
            {
			    bool is_first;
                UnityEngineBounds_TypeID = getTypeId(L, typeof(UnityEngine.Bounds), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 24, UnityEngineBounds_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Bounds ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Bounds val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineBounds_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Bounds");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Bounds");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Bounds)objectCasters.GetCaster(typeof(UnityEngine.Bounds))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineBounds(RealStatePtr L, int index, UnityEngine.Bounds val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineBounds_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Bounds");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Bounds ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineRay2D_TypeID = -1;
        public void PushUnityEngineRay2D(RealStatePtr L, UnityEngine.Ray2D val)
        {
            if (UnityEngineRay2D_TypeID == -1)
            {
			    bool is_first;
                UnityEngineRay2D_TypeID = getTypeId(L, typeof(UnityEngine.Ray2D), out is_first);
				
            }
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 16, UnityEngineRay2D_TypeID);
            if (!CopyByValue.Pack(buff, 0, val))
            {
                throw new Exception("pack fail fail for UnityEngine.Ray2D ,value="+val);
            }
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Ray2D val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRay2D_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Ray2D");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);if (!CopyByValue.UnPack(buff, 0, out val))
                {
                    throw new Exception("unpack fail for UnityEngine.Ray2D");
                }
            }
			else if (type ==LuaTypes.LUA_TTABLE)
			{
			    CopyByValue.UnPack(this, L, index, out val);
			}
            else
            {
                val = (UnityEngine.Ray2D)objectCasters.GetCaster(typeof(UnityEngine.Ray2D))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineRay2D(RealStatePtr L, int index, UnityEngine.Ray2D val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRay2D_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Ray2D");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  val))
                {
                    throw new Exception("pack fail for UnityEngine.Ray2D ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineAnimatorCullingMode_TypeID = -1;
		int UnityEngineAnimatorCullingMode_EnumRef = -1;
        
        public void PushUnityEngineAnimatorCullingMode(RealStatePtr L, UnityEngine.AnimatorCullingMode val)
        {
            if (UnityEngineAnimatorCullingMode_TypeID == -1)
            {
			    bool is_first;
                UnityEngineAnimatorCullingMode_TypeID = getTypeId(L, typeof(UnityEngine.AnimatorCullingMode), out is_first);
				
				if (UnityEngineAnimatorCullingMode_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.AnimatorCullingMode));
				    UnityEngineAnimatorCullingMode_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineAnimatorCullingMode_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineAnimatorCullingMode_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.AnimatorCullingMode ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineAnimatorCullingMode_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.AnimatorCullingMode val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineAnimatorCullingMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.AnimatorCullingMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.AnimatorCullingMode");
                }
				val = (UnityEngine.AnimatorCullingMode)e;
                
            }
            else
            {
                val = (UnityEngine.AnimatorCullingMode)objectCasters.GetCaster(typeof(UnityEngine.AnimatorCullingMode))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineAnimatorCullingMode(RealStatePtr L, int index, UnityEngine.AnimatorCullingMode val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineAnimatorCullingMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.AnimatorCullingMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.AnimatorCullingMode ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineSpace_TypeID = -1;
		int UnityEngineSpace_EnumRef = -1;
        
        public void PushUnityEngineSpace(RealStatePtr L, UnityEngine.Space val)
        {
            if (UnityEngineSpace_TypeID == -1)
            {
			    bool is_first;
                UnityEngineSpace_TypeID = getTypeId(L, typeof(UnityEngine.Space), out is_first);
				
				if (UnityEngineSpace_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.Space));
				    UnityEngineSpace_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineSpace_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineSpace_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.Space ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineSpace_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.Space val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineSpace_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Space");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.Space");
                }
				val = (UnityEngine.Space)e;
                
            }
            else
            {
                val = (UnityEngine.Space)objectCasters.GetCaster(typeof(UnityEngine.Space))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineSpace(RealStatePtr L, int index, UnityEngine.Space val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineSpace_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.Space");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.Space ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineForceMode_TypeID = -1;
		int UnityEngineForceMode_EnumRef = -1;
        
        public void PushUnityEngineForceMode(RealStatePtr L, UnityEngine.ForceMode val)
        {
            if (UnityEngineForceMode_TypeID == -1)
            {
			    bool is_first;
                UnityEngineForceMode_TypeID = getTypeId(L, typeof(UnityEngine.ForceMode), out is_first);
				
				if (UnityEngineForceMode_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.ForceMode));
				    UnityEngineForceMode_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineForceMode_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineForceMode_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.ForceMode ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineForceMode_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.ForceMode val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineForceMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.ForceMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.ForceMode");
                }
				val = (UnityEngine.ForceMode)e;
                
            }
            else
            {
                val = (UnityEngine.ForceMode)objectCasters.GetCaster(typeof(UnityEngine.ForceMode))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineForceMode(RealStatePtr L, int index, UnityEngine.ForceMode val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineForceMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.ForceMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.ForceMode ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEnginePrimitiveType_TypeID = -1;
		int UnityEnginePrimitiveType_EnumRef = -1;
        
        public void PushUnityEnginePrimitiveType(RealStatePtr L, UnityEngine.PrimitiveType val)
        {
            if (UnityEnginePrimitiveType_TypeID == -1)
            {
			    bool is_first;
                UnityEnginePrimitiveType_TypeID = getTypeId(L, typeof(UnityEngine.PrimitiveType), out is_first);
				
				if (UnityEnginePrimitiveType_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.PrimitiveType));
				    UnityEnginePrimitiveType_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEnginePrimitiveType_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEnginePrimitiveType_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.PrimitiveType ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEnginePrimitiveType_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.PrimitiveType val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEnginePrimitiveType_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.PrimitiveType");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.PrimitiveType");
                }
				val = (UnityEngine.PrimitiveType)e;
                
            }
            else
            {
                val = (UnityEngine.PrimitiveType)objectCasters.GetCaster(typeof(UnityEngine.PrimitiveType))(L, index, null);
            }
        }
		
        public void UpdateUnityEnginePrimitiveType(RealStatePtr L, int index, UnityEngine.PrimitiveType val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEnginePrimitiveType_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.PrimitiveType");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.PrimitiveType ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineQueryTriggerInteraction_TypeID = -1;
		int UnityEngineQueryTriggerInteraction_EnumRef = -1;
        
        public void PushUnityEngineQueryTriggerInteraction(RealStatePtr L, UnityEngine.QueryTriggerInteraction val)
        {
            if (UnityEngineQueryTriggerInteraction_TypeID == -1)
            {
			    bool is_first;
                UnityEngineQueryTriggerInteraction_TypeID = getTypeId(L, typeof(UnityEngine.QueryTriggerInteraction), out is_first);
				
				if (UnityEngineQueryTriggerInteraction_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.QueryTriggerInteraction));
				    UnityEngineQueryTriggerInteraction_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineQueryTriggerInteraction_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineQueryTriggerInteraction_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.QueryTriggerInteraction ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineQueryTriggerInteraction_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.QueryTriggerInteraction val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineQueryTriggerInteraction_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.QueryTriggerInteraction");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.QueryTriggerInteraction");
                }
				val = (UnityEngine.QueryTriggerInteraction)e;
                
            }
            else
            {
                val = (UnityEngine.QueryTriggerInteraction)objectCasters.GetCaster(typeof(UnityEngine.QueryTriggerInteraction))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineQueryTriggerInteraction(RealStatePtr L, int index, UnityEngine.QueryTriggerInteraction val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineQueryTriggerInteraction_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.QueryTriggerInteraction");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.QueryTriggerInteraction ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineCollisionDetectionMode_TypeID = -1;
		int UnityEngineCollisionDetectionMode_EnumRef = -1;
        
        public void PushUnityEngineCollisionDetectionMode(RealStatePtr L, UnityEngine.CollisionDetectionMode val)
        {
            if (UnityEngineCollisionDetectionMode_TypeID == -1)
            {
			    bool is_first;
                UnityEngineCollisionDetectionMode_TypeID = getTypeId(L, typeof(UnityEngine.CollisionDetectionMode), out is_first);
				
				if (UnityEngineCollisionDetectionMode_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.CollisionDetectionMode));
				    UnityEngineCollisionDetectionMode_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineCollisionDetectionMode_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineCollisionDetectionMode_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.CollisionDetectionMode ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineCollisionDetectionMode_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.CollisionDetectionMode val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineCollisionDetectionMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.CollisionDetectionMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.CollisionDetectionMode");
                }
				val = (UnityEngine.CollisionDetectionMode)e;
                
            }
            else
            {
                val = (UnityEngine.CollisionDetectionMode)objectCasters.GetCaster(typeof(UnityEngine.CollisionDetectionMode))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineCollisionDetectionMode(RealStatePtr L, int index, UnityEngine.CollisionDetectionMode val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineCollisionDetectionMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.CollisionDetectionMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.CollisionDetectionMode ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineRigidbodyConstraints_TypeID = -1;
		int UnityEngineRigidbodyConstraints_EnumRef = -1;
        
        public void PushUnityEngineRigidbodyConstraints(RealStatePtr L, UnityEngine.RigidbodyConstraints val)
        {
            if (UnityEngineRigidbodyConstraints_TypeID == -1)
            {
			    bool is_first;
                UnityEngineRigidbodyConstraints_TypeID = getTypeId(L, typeof(UnityEngine.RigidbodyConstraints), out is_first);
				
				if (UnityEngineRigidbodyConstraints_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.RigidbodyConstraints));
				    UnityEngineRigidbodyConstraints_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineRigidbodyConstraints_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineRigidbodyConstraints_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.RigidbodyConstraints ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineRigidbodyConstraints_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.RigidbodyConstraints val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRigidbodyConstraints_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.RigidbodyConstraints");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.RigidbodyConstraints");
                }
				val = (UnityEngine.RigidbodyConstraints)e;
                
            }
            else
            {
                val = (UnityEngine.RigidbodyConstraints)objectCasters.GetCaster(typeof(UnityEngine.RigidbodyConstraints))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineRigidbodyConstraints(RealStatePtr L, int index, UnityEngine.RigidbodyConstraints val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRigidbodyConstraints_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.RigidbodyConstraints");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.RigidbodyConstraints ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineRigidbodyInterpolation_TypeID = -1;
		int UnityEngineRigidbodyInterpolation_EnumRef = -1;
        
        public void PushUnityEngineRigidbodyInterpolation(RealStatePtr L, UnityEngine.RigidbodyInterpolation val)
        {
            if (UnityEngineRigidbodyInterpolation_TypeID == -1)
            {
			    bool is_first;
                UnityEngineRigidbodyInterpolation_TypeID = getTypeId(L, typeof(UnityEngine.RigidbodyInterpolation), out is_first);
				
				if (UnityEngineRigidbodyInterpolation_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.RigidbodyInterpolation));
				    UnityEngineRigidbodyInterpolation_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineRigidbodyInterpolation_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineRigidbodyInterpolation_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.RigidbodyInterpolation ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineRigidbodyInterpolation_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.RigidbodyInterpolation val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRigidbodyInterpolation_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.RigidbodyInterpolation");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.RigidbodyInterpolation");
                }
				val = (UnityEngine.RigidbodyInterpolation)e;
                
            }
            else
            {
                val = (UnityEngine.RigidbodyInterpolation)objectCasters.GetCaster(typeof(UnityEngine.RigidbodyInterpolation))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineRigidbodyInterpolation(RealStatePtr L, int index, UnityEngine.RigidbodyInterpolation val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRigidbodyInterpolation_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.RigidbodyInterpolation");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.RigidbodyInterpolation ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineLightType_TypeID = -1;
		int UnityEngineLightType_EnumRef = -1;
        
        public void PushUnityEngineLightType(RealStatePtr L, UnityEngine.LightType val)
        {
            if (UnityEngineLightType_TypeID == -1)
            {
			    bool is_first;
                UnityEngineLightType_TypeID = getTypeId(L, typeof(UnityEngine.LightType), out is_first);
				
				if (UnityEngineLightType_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.LightType));
				    UnityEngineLightType_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineLightType_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineLightType_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.LightType ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineLightType_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.LightType val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineLightType_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.LightType");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.LightType");
                }
				val = (UnityEngine.LightType)e;
                
            }
            else
            {
                val = (UnityEngine.LightType)objectCasters.GetCaster(typeof(UnityEngine.LightType))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineLightType(RealStatePtr L, int index, UnityEngine.LightType val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineLightType_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.LightType");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.LightType ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineLightShadows_TypeID = -1;
		int UnityEngineLightShadows_EnumRef = -1;
        
        public void PushUnityEngineLightShadows(RealStatePtr L, UnityEngine.LightShadows val)
        {
            if (UnityEngineLightShadows_TypeID == -1)
            {
			    bool is_first;
                UnityEngineLightShadows_TypeID = getTypeId(L, typeof(UnityEngine.LightShadows), out is_first);
				
				if (UnityEngineLightShadows_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.LightShadows));
				    UnityEngineLightShadows_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineLightShadows_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineLightShadows_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.LightShadows ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineLightShadows_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.LightShadows val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineLightShadows_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.LightShadows");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.LightShadows");
                }
				val = (UnityEngine.LightShadows)e;
                
            }
            else
            {
                val = (UnityEngine.LightShadows)objectCasters.GetCaster(typeof(UnityEngine.LightShadows))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineLightShadows(RealStatePtr L, int index, UnityEngine.LightShadows val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineLightShadows_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.LightShadows");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.LightShadows ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineCameraClearFlags_TypeID = -1;
		int UnityEngineCameraClearFlags_EnumRef = -1;
        
        public void PushUnityEngineCameraClearFlags(RealStatePtr L, UnityEngine.CameraClearFlags val)
        {
            if (UnityEngineCameraClearFlags_TypeID == -1)
            {
			    bool is_first;
                UnityEngineCameraClearFlags_TypeID = getTypeId(L, typeof(UnityEngine.CameraClearFlags), out is_first);
				
				if (UnityEngineCameraClearFlags_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.CameraClearFlags));
				    UnityEngineCameraClearFlags_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineCameraClearFlags_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineCameraClearFlags_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.CameraClearFlags ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineCameraClearFlags_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.CameraClearFlags val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineCameraClearFlags_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.CameraClearFlags");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.CameraClearFlags");
                }
				val = (UnityEngine.CameraClearFlags)e;
                
            }
            else
            {
                val = (UnityEngine.CameraClearFlags)objectCasters.GetCaster(typeof(UnityEngine.CameraClearFlags))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineCameraClearFlags(RealStatePtr L, int index, UnityEngine.CameraClearFlags val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineCameraClearFlags_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.CameraClearFlags");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.CameraClearFlags ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineTextureWrapMode_TypeID = -1;
		int UnityEngineTextureWrapMode_EnumRef = -1;
        
        public void PushUnityEngineTextureWrapMode(RealStatePtr L, UnityEngine.TextureWrapMode val)
        {
            if (UnityEngineTextureWrapMode_TypeID == -1)
            {
			    bool is_first;
                UnityEngineTextureWrapMode_TypeID = getTypeId(L, typeof(UnityEngine.TextureWrapMode), out is_first);
				
				if (UnityEngineTextureWrapMode_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.TextureWrapMode));
				    UnityEngineTextureWrapMode_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineTextureWrapMode_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineTextureWrapMode_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.TextureWrapMode ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineTextureWrapMode_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.TextureWrapMode val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineTextureWrapMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.TextureWrapMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.TextureWrapMode");
                }
				val = (UnityEngine.TextureWrapMode)e;
                
            }
            else
            {
                val = (UnityEngine.TextureWrapMode)objectCasters.GetCaster(typeof(UnityEngine.TextureWrapMode))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineTextureWrapMode(RealStatePtr L, int index, UnityEngine.TextureWrapMode val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineTextureWrapMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.TextureWrapMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.TextureWrapMode ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineFilterMode_TypeID = -1;
		int UnityEngineFilterMode_EnumRef = -1;
        
        public void PushUnityEngineFilterMode(RealStatePtr L, UnityEngine.FilterMode val)
        {
            if (UnityEngineFilterMode_TypeID == -1)
            {
			    bool is_first;
                UnityEngineFilterMode_TypeID = getTypeId(L, typeof(UnityEngine.FilterMode), out is_first);
				
				if (UnityEngineFilterMode_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.FilterMode));
				    UnityEngineFilterMode_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineFilterMode_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineFilterMode_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.FilterMode ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineFilterMode_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.FilterMode val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineFilterMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.FilterMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.FilterMode");
                }
				val = (UnityEngine.FilterMode)e;
                
            }
            else
            {
                val = (UnityEngine.FilterMode)objectCasters.GetCaster(typeof(UnityEngine.FilterMode))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineFilterMode(RealStatePtr L, int index, UnityEngine.FilterMode val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineFilterMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.FilterMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.FilterMode ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineRenderMode_TypeID = -1;
		int UnityEngineRenderMode_EnumRef = -1;
        
        public void PushUnityEngineRenderMode(RealStatePtr L, UnityEngine.RenderMode val)
        {
            if (UnityEngineRenderMode_TypeID == -1)
            {
			    bool is_first;
                UnityEngineRenderMode_TypeID = getTypeId(L, typeof(UnityEngine.RenderMode), out is_first);
				
				if (UnityEngineRenderMode_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.RenderMode));
				    UnityEngineRenderMode_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineRenderMode_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineRenderMode_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.RenderMode ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineRenderMode_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.RenderMode val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRenderMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.RenderMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.RenderMode");
                }
				val = (UnityEngine.RenderMode)e;
                
            }
            else
            {
                val = (UnityEngine.RenderMode)objectCasters.GetCaster(typeof(UnityEngine.RenderMode))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineRenderMode(RealStatePtr L, int index, UnityEngine.RenderMode val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRenderMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.RenderMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.RenderMode ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineSendMessageOptions_TypeID = -1;
		int UnityEngineSendMessageOptions_EnumRef = -1;
        
        public void PushUnityEngineSendMessageOptions(RealStatePtr L, UnityEngine.SendMessageOptions val)
        {
            if (UnityEngineSendMessageOptions_TypeID == -1)
            {
			    bool is_first;
                UnityEngineSendMessageOptions_TypeID = getTypeId(L, typeof(UnityEngine.SendMessageOptions), out is_first);
				
				if (UnityEngineSendMessageOptions_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.SendMessageOptions));
				    UnityEngineSendMessageOptions_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineSendMessageOptions_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineSendMessageOptions_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.SendMessageOptions ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineSendMessageOptions_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.SendMessageOptions val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineSendMessageOptions_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.SendMessageOptions");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.SendMessageOptions");
                }
				val = (UnityEngine.SendMessageOptions)e;
                
            }
            else
            {
                val = (UnityEngine.SendMessageOptions)objectCasters.GetCaster(typeof(UnityEngine.SendMessageOptions))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineSendMessageOptions(RealStatePtr L, int index, UnityEngine.SendMessageOptions val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineSendMessageOptions_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.SendMessageOptions");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.SendMessageOptions ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineFindObjectsSortMode_TypeID = -1;
		int UnityEngineFindObjectsSortMode_EnumRef = -1;
        
        public void PushUnityEngineFindObjectsSortMode(RealStatePtr L, UnityEngine.FindObjectsSortMode val)
        {
            if (UnityEngineFindObjectsSortMode_TypeID == -1)
            {
			    bool is_first;
                UnityEngineFindObjectsSortMode_TypeID = getTypeId(L, typeof(UnityEngine.FindObjectsSortMode), out is_first);
				
				if (UnityEngineFindObjectsSortMode_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.FindObjectsSortMode));
				    UnityEngineFindObjectsSortMode_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineFindObjectsSortMode_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineFindObjectsSortMode_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.FindObjectsSortMode ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineFindObjectsSortMode_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.FindObjectsSortMode val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineFindObjectsSortMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.FindObjectsSortMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.FindObjectsSortMode");
                }
				val = (UnityEngine.FindObjectsSortMode)e;
                
            }
            else
            {
                val = (UnityEngine.FindObjectsSortMode)objectCasters.GetCaster(typeof(UnityEngine.FindObjectsSortMode))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineFindObjectsSortMode(RealStatePtr L, int index, UnityEngine.FindObjectsSortMode val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineFindObjectsSortMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.FindObjectsSortMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.FindObjectsSortMode ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineFindObjectsInactive_TypeID = -1;
		int UnityEngineFindObjectsInactive_EnumRef = -1;
        
        public void PushUnityEngineFindObjectsInactive(RealStatePtr L, UnityEngine.FindObjectsInactive val)
        {
            if (UnityEngineFindObjectsInactive_TypeID == -1)
            {
			    bool is_first;
                UnityEngineFindObjectsInactive_TypeID = getTypeId(L, typeof(UnityEngine.FindObjectsInactive), out is_first);
				
				if (UnityEngineFindObjectsInactive_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.FindObjectsInactive));
				    UnityEngineFindObjectsInactive_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineFindObjectsInactive_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineFindObjectsInactive_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.FindObjectsInactive ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineFindObjectsInactive_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.FindObjectsInactive val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineFindObjectsInactive_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.FindObjectsInactive");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.FindObjectsInactive");
                }
				val = (UnityEngine.FindObjectsInactive)e;
                
            }
            else
            {
                val = (UnityEngine.FindObjectsInactive)objectCasters.GetCaster(typeof(UnityEngine.FindObjectsInactive))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineFindObjectsInactive(RealStatePtr L, int index, UnityEngine.FindObjectsInactive val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineFindObjectsInactive_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.FindObjectsInactive");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.FindObjectsInactive ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineHideFlags_TypeID = -1;
		int UnityEngineHideFlags_EnumRef = -1;
        
        public void PushUnityEngineHideFlags(RealStatePtr L, UnityEngine.HideFlags val)
        {
            if (UnityEngineHideFlags_TypeID == -1)
            {
			    bool is_first;
                UnityEngineHideFlags_TypeID = getTypeId(L, typeof(UnityEngine.HideFlags), out is_first);
				
				if (UnityEngineHideFlags_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.HideFlags));
				    UnityEngineHideFlags_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineHideFlags_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineHideFlags_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.HideFlags ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineHideFlags_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.HideFlags val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineHideFlags_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.HideFlags");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.HideFlags");
                }
				val = (UnityEngine.HideFlags)e;
                
            }
            else
            {
                val = (UnityEngine.HideFlags)objectCasters.GetCaster(typeof(UnityEngine.HideFlags))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineHideFlags(RealStatePtr L, int index, UnityEngine.HideFlags val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineHideFlags_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.HideFlags");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.HideFlags ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineRuntimePlatform_TypeID = -1;
		int UnityEngineRuntimePlatform_EnumRef = -1;
        
        public void PushUnityEngineRuntimePlatform(RealStatePtr L, UnityEngine.RuntimePlatform val)
        {
            if (UnityEngineRuntimePlatform_TypeID == -1)
            {
			    bool is_first;
                UnityEngineRuntimePlatform_TypeID = getTypeId(L, typeof(UnityEngine.RuntimePlatform), out is_first);
				
				if (UnityEngineRuntimePlatform_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.RuntimePlatform));
				    UnityEngineRuntimePlatform_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineRuntimePlatform_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineRuntimePlatform_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.RuntimePlatform ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineRuntimePlatform_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.RuntimePlatform val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRuntimePlatform_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.RuntimePlatform");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.RuntimePlatform");
                }
				val = (UnityEngine.RuntimePlatform)e;
                
            }
            else
            {
                val = (UnityEngine.RuntimePlatform)objectCasters.GetCaster(typeof(UnityEngine.RuntimePlatform))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineRuntimePlatform(RealStatePtr L, int index, UnityEngine.RuntimePlatform val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineRuntimePlatform_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.RuntimePlatform");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.RuntimePlatform ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineNetworkReachability_TypeID = -1;
		int UnityEngineNetworkReachability_EnumRef = -1;
        
        public void PushUnityEngineNetworkReachability(RealStatePtr L, UnityEngine.NetworkReachability val)
        {
            if (UnityEngineNetworkReachability_TypeID == -1)
            {
			    bool is_first;
                UnityEngineNetworkReachability_TypeID = getTypeId(L, typeof(UnityEngine.NetworkReachability), out is_first);
				
				if (UnityEngineNetworkReachability_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.NetworkReachability));
				    UnityEngineNetworkReachability_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineNetworkReachability_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineNetworkReachability_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.NetworkReachability ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineNetworkReachability_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.NetworkReachability val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineNetworkReachability_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.NetworkReachability");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.NetworkReachability");
                }
				val = (UnityEngine.NetworkReachability)e;
                
            }
            else
            {
                val = (UnityEngine.NetworkReachability)objectCasters.GetCaster(typeof(UnityEngine.NetworkReachability))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineNetworkReachability(RealStatePtr L, int index, UnityEngine.NetworkReachability val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineNetworkReachability_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.NetworkReachability");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.NetworkReachability ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineAudioRolloffMode_TypeID = -1;
		int UnityEngineAudioRolloffMode_EnumRef = -1;
        
        public void PushUnityEngineAudioRolloffMode(RealStatePtr L, UnityEngine.AudioRolloffMode val)
        {
            if (UnityEngineAudioRolloffMode_TypeID == -1)
            {
			    bool is_first;
                UnityEngineAudioRolloffMode_TypeID = getTypeId(L, typeof(UnityEngine.AudioRolloffMode), out is_first);
				
				if (UnityEngineAudioRolloffMode_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.AudioRolloffMode));
				    UnityEngineAudioRolloffMode_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineAudioRolloffMode_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineAudioRolloffMode_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.AudioRolloffMode ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineAudioRolloffMode_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.AudioRolloffMode val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineAudioRolloffMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.AudioRolloffMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.AudioRolloffMode");
                }
				val = (UnityEngine.AudioRolloffMode)e;
                
            }
            else
            {
                val = (UnityEngine.AudioRolloffMode)objectCasters.GetCaster(typeof(UnityEngine.AudioRolloffMode))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineAudioRolloffMode(RealStatePtr L, int index, UnityEngine.AudioRolloffMode val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineAudioRolloffMode_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.AudioRolloffMode");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.AudioRolloffMode ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineParticleSystemStopBehavior_TypeID = -1;
		int UnityEngineParticleSystemStopBehavior_EnumRef = -1;
        
        public void PushUnityEngineParticleSystemStopBehavior(RealStatePtr L, UnityEngine.ParticleSystemStopBehavior val)
        {
            if (UnityEngineParticleSystemStopBehavior_TypeID == -1)
            {
			    bool is_first;
                UnityEngineParticleSystemStopBehavior_TypeID = getTypeId(L, typeof(UnityEngine.ParticleSystemStopBehavior), out is_first);
				
				if (UnityEngineParticleSystemStopBehavior_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.ParticleSystemStopBehavior));
				    UnityEngineParticleSystemStopBehavior_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineParticleSystemStopBehavior_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineParticleSystemStopBehavior_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.ParticleSystemStopBehavior ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineParticleSystemStopBehavior_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.ParticleSystemStopBehavior val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineParticleSystemStopBehavior_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.ParticleSystemStopBehavior");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.ParticleSystemStopBehavior");
                }
				val = (UnityEngine.ParticleSystemStopBehavior)e;
                
            }
            else
            {
                val = (UnityEngine.ParticleSystemStopBehavior)objectCasters.GetCaster(typeof(UnityEngine.ParticleSystemStopBehavior))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineParticleSystemStopBehavior(RealStatePtr L, int index, UnityEngine.ParticleSystemStopBehavior val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineParticleSystemStopBehavior_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.ParticleSystemStopBehavior");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.ParticleSystemStopBehavior ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineAINavMeshPathStatus_TypeID = -1;
		int UnityEngineAINavMeshPathStatus_EnumRef = -1;
        
        public void PushUnityEngineAINavMeshPathStatus(RealStatePtr L, UnityEngine.AI.NavMeshPathStatus val)
        {
            if (UnityEngineAINavMeshPathStatus_TypeID == -1)
            {
			    bool is_first;
                UnityEngineAINavMeshPathStatus_TypeID = getTypeId(L, typeof(UnityEngine.AI.NavMeshPathStatus), out is_first);
				
				if (UnityEngineAINavMeshPathStatus_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.AI.NavMeshPathStatus));
				    UnityEngineAINavMeshPathStatus_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineAINavMeshPathStatus_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineAINavMeshPathStatus_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.AI.NavMeshPathStatus ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineAINavMeshPathStatus_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.AI.NavMeshPathStatus val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineAINavMeshPathStatus_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.AI.NavMeshPathStatus");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.AI.NavMeshPathStatus");
                }
				val = (UnityEngine.AI.NavMeshPathStatus)e;
                
            }
            else
            {
                val = (UnityEngine.AI.NavMeshPathStatus)objectCasters.GetCaster(typeof(UnityEngine.AI.NavMeshPathStatus))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineAINavMeshPathStatus(RealStatePtr L, int index, UnityEngine.AI.NavMeshPathStatus val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineAINavMeshPathStatus_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.AI.NavMeshPathStatus");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.AI.NavMeshPathStatus ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int UnityEngineAIObstacleAvoidanceType_TypeID = -1;
		int UnityEngineAIObstacleAvoidanceType_EnumRef = -1;
        
        public void PushUnityEngineAIObstacleAvoidanceType(RealStatePtr L, UnityEngine.AI.ObstacleAvoidanceType val)
        {
            if (UnityEngineAIObstacleAvoidanceType_TypeID == -1)
            {
			    bool is_first;
                UnityEngineAIObstacleAvoidanceType_TypeID = getTypeId(L, typeof(UnityEngine.AI.ObstacleAvoidanceType), out is_first);
				
				if (UnityEngineAIObstacleAvoidanceType_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(UnityEngine.AI.ObstacleAvoidanceType));
				    UnityEngineAIObstacleAvoidanceType_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, UnityEngineAIObstacleAvoidanceType_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, UnityEngineAIObstacleAvoidanceType_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for UnityEngine.AI.ObstacleAvoidanceType ,value="+val);
            }
			
			LuaAPI.lua_getref(L, UnityEngineAIObstacleAvoidanceType_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out UnityEngine.AI.ObstacleAvoidanceType val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineAIObstacleAvoidanceType_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.AI.ObstacleAvoidanceType");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for UnityEngine.AI.ObstacleAvoidanceType");
                }
				val = (UnityEngine.AI.ObstacleAvoidanceType)e;
                
            }
            else
            {
                val = (UnityEngine.AI.ObstacleAvoidanceType)objectCasters.GetCaster(typeof(UnityEngine.AI.ObstacleAvoidanceType))(L, index, null);
            }
        }
		
        public void UpdateUnityEngineAIObstacleAvoidanceType(RealStatePtr L, int index, UnityEngine.AI.ObstacleAvoidanceType val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != UnityEngineAIObstacleAvoidanceType_TypeID)
				{
				    throw new Exception("invalid userdata for UnityEngine.AI.ObstacleAvoidanceType");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for UnityEngine.AI.ObstacleAvoidanceType ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        int TMProTextAlignmentOptions_TypeID = -1;
		int TMProTextAlignmentOptions_EnumRef = -1;
        
        public void PushTMProTextAlignmentOptions(RealStatePtr L, TMPro.TextAlignmentOptions val)
        {
            if (TMProTextAlignmentOptions_TypeID == -1)
            {
			    bool is_first;
                TMProTextAlignmentOptions_TypeID = getTypeId(L, typeof(TMPro.TextAlignmentOptions), out is_first);
				
				if (TMProTextAlignmentOptions_EnumRef == -1)
				{
				    Utils.LoadCSTable(L, typeof(TMPro.TextAlignmentOptions));
				    TMProTextAlignmentOptions_EnumRef = LuaAPI.luaL_ref(L, LuaIndexes.LUA_REGISTRYINDEX);
				}
				
            }
			
			if (LuaAPI.xlua_tryget_cachedud(L, (int)val, TMProTextAlignmentOptions_EnumRef) == 1)
            {
			    return;
			}
			
            IntPtr buff = LuaAPI.xlua_pushstruct(L, 4, TMProTextAlignmentOptions_TypeID);
            if (!CopyByValue.Pack(buff, 0, (int)val))
            {
                throw new Exception("pack fail fail for TMPro.TextAlignmentOptions ,value="+val);
            }
			
			LuaAPI.lua_getref(L, TMProTextAlignmentOptions_EnumRef);
			LuaAPI.lua_pushvalue(L, -2);
			LuaAPI.xlua_rawseti(L, -2, (int)val);
			LuaAPI.lua_pop(L, 1);
			
        }
		
        public void Get(RealStatePtr L, int index, out TMPro.TextAlignmentOptions val)
        {
		    LuaTypes type = LuaAPI.lua_type(L, index);
            if (type == LuaTypes.LUA_TUSERDATA )
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != TMProTextAlignmentOptions_TypeID)
				{
				    throw new Exception("invalid userdata for TMPro.TextAlignmentOptions");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
				int e;
                if (!CopyByValue.UnPack(buff, 0, out e))
                {
                    throw new Exception("unpack fail for TMPro.TextAlignmentOptions");
                }
				val = (TMPro.TextAlignmentOptions)e;
                
            }
            else
            {
                val = (TMPro.TextAlignmentOptions)objectCasters.GetCaster(typeof(TMPro.TextAlignmentOptions))(L, index, null);
            }
        }
		
        public void UpdateTMProTextAlignmentOptions(RealStatePtr L, int index, TMPro.TextAlignmentOptions val)
        {
		    
            if (LuaAPI.lua_type(L, index) == LuaTypes.LUA_TUSERDATA)
            {
			    if (LuaAPI.xlua_gettypeid(L, index) != TMProTextAlignmentOptions_TypeID)
				{
				    throw new Exception("invalid userdata for TMPro.TextAlignmentOptions");
				}
				
                IntPtr buff = LuaAPI.lua_touserdata(L, index);
                if (!CopyByValue.Pack(buff, 0,  (int)val))
                {
                    throw new Exception("pack fail for TMPro.TextAlignmentOptions ,value="+val);
                }
            }
			
            else
            {
                throw new Exception("try to update a data with lua type:" + LuaAPI.lua_type(L, index));
            }
        }
        
        
		// table cast optimze
		
        
    }
	
	public partial class StaticLuaCallbacks
    {
	    internal static bool __tryArrayGet(Type type, RealStatePtr L, ObjectTranslator translator, object obj, int index)
		{
		
			if (type == typeof(UnityEngine.Color32[]))
			{
			    UnityEngine.Color32[] array = obj as UnityEngine.Color32[];
				translator.PushUnityEngineColor32(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Rect[]))
			{
			    UnityEngine.Rect[] array = obj as UnityEngine.Rect[];
				translator.PushUnityEngineRect(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Keyframe[]))
			{
			    UnityEngine.Keyframe[] array = obj as UnityEngine.Keyframe[];
				translator.PushUnityEngineKeyframe(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Vector2[]))
			{
			    UnityEngine.Vector2[] array = obj as UnityEngine.Vector2[];
				translator.PushUnityEngineVector2(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Vector3[]))
			{
			    UnityEngine.Vector3[] array = obj as UnityEngine.Vector3[];
				translator.PushUnityEngineVector3(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Vector4[]))
			{
			    UnityEngine.Vector4[] array = obj as UnityEngine.Vector4[];
				translator.PushUnityEngineVector4(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Color[]))
			{
			    UnityEngine.Color[] array = obj as UnityEngine.Color[];
				translator.PushUnityEngineColor(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Quaternion[]))
			{
			    UnityEngine.Quaternion[] array = obj as UnityEngine.Quaternion[];
				translator.PushUnityEngineQuaternion(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Ray[]))
			{
			    UnityEngine.Ray[] array = obj as UnityEngine.Ray[];
				translator.PushUnityEngineRay(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Bounds[]))
			{
			    UnityEngine.Bounds[] array = obj as UnityEngine.Bounds[];
				translator.PushUnityEngineBounds(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Ray2D[]))
			{
			    UnityEngine.Ray2D[] array = obj as UnityEngine.Ray2D[];
				translator.PushUnityEngineRay2D(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.AnimatorCullingMode[]))
			{
			    UnityEngine.AnimatorCullingMode[] array = obj as UnityEngine.AnimatorCullingMode[];
				translator.PushUnityEngineAnimatorCullingMode(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.Space[]))
			{
			    UnityEngine.Space[] array = obj as UnityEngine.Space[];
				translator.PushUnityEngineSpace(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.ForceMode[]))
			{
			    UnityEngine.ForceMode[] array = obj as UnityEngine.ForceMode[];
				translator.PushUnityEngineForceMode(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.PrimitiveType[]))
			{
			    UnityEngine.PrimitiveType[] array = obj as UnityEngine.PrimitiveType[];
				translator.PushUnityEnginePrimitiveType(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.QueryTriggerInteraction[]))
			{
			    UnityEngine.QueryTriggerInteraction[] array = obj as UnityEngine.QueryTriggerInteraction[];
				translator.PushUnityEngineQueryTriggerInteraction(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.CollisionDetectionMode[]))
			{
			    UnityEngine.CollisionDetectionMode[] array = obj as UnityEngine.CollisionDetectionMode[];
				translator.PushUnityEngineCollisionDetectionMode(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.RigidbodyConstraints[]))
			{
			    UnityEngine.RigidbodyConstraints[] array = obj as UnityEngine.RigidbodyConstraints[];
				translator.PushUnityEngineRigidbodyConstraints(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.RigidbodyInterpolation[]))
			{
			    UnityEngine.RigidbodyInterpolation[] array = obj as UnityEngine.RigidbodyInterpolation[];
				translator.PushUnityEngineRigidbodyInterpolation(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.LightType[]))
			{
			    UnityEngine.LightType[] array = obj as UnityEngine.LightType[];
				translator.PushUnityEngineLightType(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.LightShadows[]))
			{
			    UnityEngine.LightShadows[] array = obj as UnityEngine.LightShadows[];
				translator.PushUnityEngineLightShadows(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.CameraClearFlags[]))
			{
			    UnityEngine.CameraClearFlags[] array = obj as UnityEngine.CameraClearFlags[];
				translator.PushUnityEngineCameraClearFlags(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.TextureWrapMode[]))
			{
			    UnityEngine.TextureWrapMode[] array = obj as UnityEngine.TextureWrapMode[];
				translator.PushUnityEngineTextureWrapMode(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.FilterMode[]))
			{
			    UnityEngine.FilterMode[] array = obj as UnityEngine.FilterMode[];
				translator.PushUnityEngineFilterMode(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.RenderMode[]))
			{
			    UnityEngine.RenderMode[] array = obj as UnityEngine.RenderMode[];
				translator.PushUnityEngineRenderMode(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.SendMessageOptions[]))
			{
			    UnityEngine.SendMessageOptions[] array = obj as UnityEngine.SendMessageOptions[];
				translator.PushUnityEngineSendMessageOptions(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.FindObjectsSortMode[]))
			{
			    UnityEngine.FindObjectsSortMode[] array = obj as UnityEngine.FindObjectsSortMode[];
				translator.PushUnityEngineFindObjectsSortMode(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.FindObjectsInactive[]))
			{
			    UnityEngine.FindObjectsInactive[] array = obj as UnityEngine.FindObjectsInactive[];
				translator.PushUnityEngineFindObjectsInactive(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.HideFlags[]))
			{
			    UnityEngine.HideFlags[] array = obj as UnityEngine.HideFlags[];
				translator.PushUnityEngineHideFlags(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.RuntimePlatform[]))
			{
			    UnityEngine.RuntimePlatform[] array = obj as UnityEngine.RuntimePlatform[];
				translator.PushUnityEngineRuntimePlatform(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.NetworkReachability[]))
			{
			    UnityEngine.NetworkReachability[] array = obj as UnityEngine.NetworkReachability[];
				translator.PushUnityEngineNetworkReachability(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.AudioRolloffMode[]))
			{
			    UnityEngine.AudioRolloffMode[] array = obj as UnityEngine.AudioRolloffMode[];
				translator.PushUnityEngineAudioRolloffMode(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.ParticleSystemStopBehavior[]))
			{
			    UnityEngine.ParticleSystemStopBehavior[] array = obj as UnityEngine.ParticleSystemStopBehavior[];
				translator.PushUnityEngineParticleSystemStopBehavior(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.AI.NavMeshPathStatus[]))
			{
			    UnityEngine.AI.NavMeshPathStatus[] array = obj as UnityEngine.AI.NavMeshPathStatus[];
				translator.PushUnityEngineAINavMeshPathStatus(L, array[index]);
				return true;
			}
			else if (type == typeof(UnityEngine.AI.ObstacleAvoidanceType[]))
			{
			    UnityEngine.AI.ObstacleAvoidanceType[] array = obj as UnityEngine.AI.ObstacleAvoidanceType[];
				translator.PushUnityEngineAIObstacleAvoidanceType(L, array[index]);
				return true;
			}
			else if (type == typeof(TMPro.TextAlignmentOptions[]))
			{
			    TMPro.TextAlignmentOptions[] array = obj as TMPro.TextAlignmentOptions[];
				translator.PushTMProTextAlignmentOptions(L, array[index]);
				return true;
			}
            return false;
		}
		
		internal static bool __tryArraySet(Type type, RealStatePtr L, ObjectTranslator translator, object obj, int array_idx, int obj_idx)
		{
		
			if (type == typeof(UnityEngine.Color32[]))
			{
			    UnityEngine.Color32[] array = obj as UnityEngine.Color32[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Rect[]))
			{
			    UnityEngine.Rect[] array = obj as UnityEngine.Rect[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Keyframe[]))
			{
			    UnityEngine.Keyframe[] array = obj as UnityEngine.Keyframe[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Vector2[]))
			{
			    UnityEngine.Vector2[] array = obj as UnityEngine.Vector2[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Vector3[]))
			{
			    UnityEngine.Vector3[] array = obj as UnityEngine.Vector3[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Vector4[]))
			{
			    UnityEngine.Vector4[] array = obj as UnityEngine.Vector4[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Color[]))
			{
			    UnityEngine.Color[] array = obj as UnityEngine.Color[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Quaternion[]))
			{
			    UnityEngine.Quaternion[] array = obj as UnityEngine.Quaternion[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Ray[]))
			{
			    UnityEngine.Ray[] array = obj as UnityEngine.Ray[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Bounds[]))
			{
			    UnityEngine.Bounds[] array = obj as UnityEngine.Bounds[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Ray2D[]))
			{
			    UnityEngine.Ray2D[] array = obj as UnityEngine.Ray2D[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.AnimatorCullingMode[]))
			{
			    UnityEngine.AnimatorCullingMode[] array = obj as UnityEngine.AnimatorCullingMode[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.Space[]))
			{
			    UnityEngine.Space[] array = obj as UnityEngine.Space[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.ForceMode[]))
			{
			    UnityEngine.ForceMode[] array = obj as UnityEngine.ForceMode[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.PrimitiveType[]))
			{
			    UnityEngine.PrimitiveType[] array = obj as UnityEngine.PrimitiveType[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.QueryTriggerInteraction[]))
			{
			    UnityEngine.QueryTriggerInteraction[] array = obj as UnityEngine.QueryTriggerInteraction[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.CollisionDetectionMode[]))
			{
			    UnityEngine.CollisionDetectionMode[] array = obj as UnityEngine.CollisionDetectionMode[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.RigidbodyConstraints[]))
			{
			    UnityEngine.RigidbodyConstraints[] array = obj as UnityEngine.RigidbodyConstraints[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.RigidbodyInterpolation[]))
			{
			    UnityEngine.RigidbodyInterpolation[] array = obj as UnityEngine.RigidbodyInterpolation[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.LightType[]))
			{
			    UnityEngine.LightType[] array = obj as UnityEngine.LightType[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.LightShadows[]))
			{
			    UnityEngine.LightShadows[] array = obj as UnityEngine.LightShadows[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.CameraClearFlags[]))
			{
			    UnityEngine.CameraClearFlags[] array = obj as UnityEngine.CameraClearFlags[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.TextureWrapMode[]))
			{
			    UnityEngine.TextureWrapMode[] array = obj as UnityEngine.TextureWrapMode[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.FilterMode[]))
			{
			    UnityEngine.FilterMode[] array = obj as UnityEngine.FilterMode[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.RenderMode[]))
			{
			    UnityEngine.RenderMode[] array = obj as UnityEngine.RenderMode[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.SendMessageOptions[]))
			{
			    UnityEngine.SendMessageOptions[] array = obj as UnityEngine.SendMessageOptions[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.FindObjectsSortMode[]))
			{
			    UnityEngine.FindObjectsSortMode[] array = obj as UnityEngine.FindObjectsSortMode[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.FindObjectsInactive[]))
			{
			    UnityEngine.FindObjectsInactive[] array = obj as UnityEngine.FindObjectsInactive[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.HideFlags[]))
			{
			    UnityEngine.HideFlags[] array = obj as UnityEngine.HideFlags[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.RuntimePlatform[]))
			{
			    UnityEngine.RuntimePlatform[] array = obj as UnityEngine.RuntimePlatform[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.NetworkReachability[]))
			{
			    UnityEngine.NetworkReachability[] array = obj as UnityEngine.NetworkReachability[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.AudioRolloffMode[]))
			{
			    UnityEngine.AudioRolloffMode[] array = obj as UnityEngine.AudioRolloffMode[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.ParticleSystemStopBehavior[]))
			{
			    UnityEngine.ParticleSystemStopBehavior[] array = obj as UnityEngine.ParticleSystemStopBehavior[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.AI.NavMeshPathStatus[]))
			{
			    UnityEngine.AI.NavMeshPathStatus[] array = obj as UnityEngine.AI.NavMeshPathStatus[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(UnityEngine.AI.ObstacleAvoidanceType[]))
			{
			    UnityEngine.AI.ObstacleAvoidanceType[] array = obj as UnityEngine.AI.ObstacleAvoidanceType[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
			else if (type == typeof(TMPro.TextAlignmentOptions[]))
			{
			    TMPro.TextAlignmentOptions[] array = obj as TMPro.TextAlignmentOptions[];
				translator.Get(L, obj_idx, out array[array_idx]);
				return true;
			}
            return false;
		}
	}
}