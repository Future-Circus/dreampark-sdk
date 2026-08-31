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
    public class DreamParkAPIProfileAPIWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.API.ProfileAPI);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 35, 13, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "BindIdentity", _m_BindIdentity_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "BindToLoggedInUser", _m_BindToLoggedInUser_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ClearIdentity", _m_ClearIdentity_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "IdentitySegment", _m_IdentitySegment_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FetchProfile", _m_FetchProfile_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "OnReady", _m_OnReady_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetItem", _m_GetItem_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetItemByType", _m_GetItemByType_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetItemByName", _m_GetItemByName_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetItemsByType", _m_GetItemsByType_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "HasItem", _m_HasItem_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "HasItemByType", _m_HasItemByType_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "HasBadge", _m_HasBadge_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "HasAchievement", _m_HasAchievement_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetAchievement", _m_GetAchievement_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetBadge", _m_GetBadge_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AwardUniqueItem", _m_AwardUniqueItem_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AwardItem", _m_AwardItem_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AwardAchievement", _m_AwardAchievement_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "CompleteAchievement", _m_CompleteAchievement_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AwardBadge", _m_AwardBadge_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "RemoveItem", _m_RemoveItem_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "RemoveBadge", _m_RemoveBadge_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SendSessionHeartbeat", _m_SendSessionHeartbeat_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AddDreamPoints", _m_AddDreamPoints_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SpendDreamPoints", _m_SpendDreamPoints_xlua_st_);
            
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnIdentityBound", _e_OnIdentityBound);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnIdentityCleared", _e_OnIdentityCleared);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnProfileLoaded", _e_OnProfileLoaded);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnInventoryChanged", _e_OnInventoryChanged);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnAchievementUpdated", _e_OnAchievementUpdated);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnBadgeAwarded", _e_OnBadgeAwarded);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnItemAwarded", _e_OnItemAwarded);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "OnDreamPointsChanged", _e_OnDreamPointsChanged);
			
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "BoundUserId", _g_get_BoundUserId);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "BoundDreamId", _g_get_BoundDreamId);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "ContentFilter", _g_get_ContentFilter);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "IsBound", _g_get_IsBound);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "IsLoaded", _g_get_IsLoaded);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "DisplayName", _g_get_DisplayName);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "AvatarUrl", _g_get_AvatarUrl);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "IsAnonymous", _g_get_IsAnonymous);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "DreamPoints", _g_get_DreamPoints);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Source", _g_get_Source);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Items", _g_get_Items);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Achievements", _g_get_Achievements);
            Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Badges", _g_get_Badges);
            
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "DreamPark.API.ProfileAPI does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BindIdentity_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& translator.Assignable<Defective.JSON.JSONObject>(L, 4)) 
                {
                    string _userId = LuaAPI.lua_tostring(L, 1);
                    string _dreamId = LuaAPI.lua_tostring(L, 2);
                    string _contentFilter = LuaAPI.lua_tostring(L, 3);
                    Defective.JSON.JSONObject _initialSnapshot = (Defective.JSON.JSONObject)translator.GetObject(L, 4, typeof(Defective.JSON.JSONObject));
                    
                    DreamPark.API.ProfileAPI.BindIdentity( _userId, _dreamId, _contentFilter, _initialSnapshot );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)) 
                {
                    string _userId = LuaAPI.lua_tostring(L, 1);
                    string _dreamId = LuaAPI.lua_tostring(L, 2);
                    string _contentFilter = LuaAPI.lua_tostring(L, 3);
                    
                    DreamPark.API.ProfileAPI.BindIdentity( _userId, _dreamId, _contentFilter );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    string _userId = LuaAPI.lua_tostring(L, 1);
                    string _dreamId = LuaAPI.lua_tostring(L, 2);
                    
                    DreamPark.API.ProfileAPI.BindIdentity( _userId, _dreamId );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.BindIdentity!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BindToLoggedInUser_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _contentFilter = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.API.ProfileAPI.BindToLoggedInUser( _contentFilter );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 0) 
                {
                    
                    DreamPark.API.ProfileAPI.BindToLoggedInUser(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.BindToLoggedInUser!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ClearIdentity_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    DreamPark.API.ProfileAPI.ClearIdentity(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_IdentitySegment_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = DreamPark.API.ProfileAPI.IdentitySegment(  );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FetchProfile_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _contentFilter = LuaAPI.lua_tostring(L, 1);
                    System.Action<bool, DreamPark.API.ProfileAPI.ProfileSnapshot> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.ProfileAPI.ProfileSnapshot>>(L, 2);
                    
                    DreamPark.API.ProfileAPI.FetchProfile( _contentFilter, _done );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_OnReady_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    System.Action _callback = translator.GetDelegate<System.Action>(L, 1);
                    
                    DreamPark.API.ProfileAPI.OnReady( _callback );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetItem_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.GetItem( _itemId );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetItemByType_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _type = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.GetItemByType( _type );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetItemByName_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _name = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.GetItemByName( _name );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetItemsByType_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _type = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.GetItemsByType( _type );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasItem_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.HasItem( _itemId );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasItemByType_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _type = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.HasItemByType( _type );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasBadge_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _badgeId = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.HasBadge( _badgeId );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_HasAchievement_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _id = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.HasAchievement( _id );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetAchievement_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _achievementId = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.GetAchievement( _achievementId );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetBadge_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _badgeId = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = DreamPark.API.ProfileAPI.GetBadge( _badgeId );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AwardUniqueItem_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<Defective.JSON.JSONObject>(L, 2)&& translator.Assignable<System.Action<bool, DreamPark.API.ProfileItem>>(L, 3)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    Defective.JSON.JSONObject _metadata = (Defective.JSON.JSONObject)translator.GetObject(L, 2, typeof(Defective.JSON.JSONObject));
                    System.Action<bool, DreamPark.API.ProfileItem> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.ProfileItem>>(L, 3);
                    
                    DreamPark.API.ProfileAPI.AwardUniqueItem( _itemId, _metadata, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<Defective.JSON.JSONObject>(L, 2)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    Defective.JSON.JSONObject _metadata = (Defective.JSON.JSONObject)translator.GetObject(L, 2, typeof(Defective.JSON.JSONObject));
                    
                    DreamPark.API.ProfileAPI.AwardUniqueItem( _itemId, _metadata );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.API.ProfileAPI.AwardUniqueItem( _itemId );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.AwardUniqueItem!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AwardItem_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)&& translator.Assignable<Defective.JSON.JSONObject>(L, 3)&& translator.Assignable<System.Action<bool, DreamPark.API.ProfileItem>>(L, 4)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    int _amount = LuaAPI.xlua_tointeger(L, 2);
                    Defective.JSON.JSONObject _metadata = (Defective.JSON.JSONObject)translator.GetObject(L, 3, typeof(Defective.JSON.JSONObject));
                    System.Action<bool, DreamPark.API.ProfileItem> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.ProfileItem>>(L, 4);
                    
                    DreamPark.API.ProfileAPI.AwardItem( _itemId, _amount, _metadata, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)&& translator.Assignable<Defective.JSON.JSONObject>(L, 3)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    int _amount = LuaAPI.xlua_tointeger(L, 2);
                    Defective.JSON.JSONObject _metadata = (Defective.JSON.JSONObject)translator.GetObject(L, 3, typeof(Defective.JSON.JSONObject));
                    
                    DreamPark.API.ProfileAPI.AwardItem( _itemId, _amount, _metadata );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    int _amount = LuaAPI.xlua_tointeger(L, 2);
                    
                    DreamPark.API.ProfileAPI.AwardItem( _itemId, _amount );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.API.ProfileAPI.AwardItem( _itemId );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.AwardItem!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AwardAchievement_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)&& translator.Assignable<System.Action<bool, DreamPark.API.ProfileAchievement>>(L, 3)) 
                {
                    string _achievementId = LuaAPI.lua_tostring(L, 1);
                    float _progress = (float)LuaAPI.lua_tonumber(L, 2);
                    System.Action<bool, DreamPark.API.ProfileAchievement> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.ProfileAchievement>>(L, 3);
                    
                    DreamPark.API.ProfileAPI.AwardAchievement( _achievementId, _progress, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)) 
                {
                    string _achievementId = LuaAPI.lua_tostring(L, 1);
                    float _progress = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    DreamPark.API.ProfileAPI.AwardAchievement( _achievementId, _progress );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _achievementId = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.API.ProfileAPI.AwardAchievement( _achievementId );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 3)&& translator.Assignable<System.Action<bool, DreamPark.API.ProfileAchievement>>(L, 4)) 
                {
                    string _achievementId = LuaAPI.lua_tostring(L, 1);
                    float _progress = (float)LuaAPI.lua_tonumber(L, 2);
                    bool _complete = LuaAPI.lua_toboolean(L, 3);
                    System.Action<bool, DreamPark.API.ProfileAchievement> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.ProfileAchievement>>(L, 4);
                    
                    DreamPark.API.ProfileAPI.AwardAchievement( _achievementId, _progress, _complete, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 3)) 
                {
                    string _achievementId = LuaAPI.lua_tostring(L, 1);
                    float _progress = (float)LuaAPI.lua_tonumber(L, 2);
                    bool _complete = LuaAPI.lua_toboolean(L, 3);
                    
                    DreamPark.API.ProfileAPI.AwardAchievement( _achievementId, _progress, _complete );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.AwardAchievement!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_CompleteAchievement_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.Action<bool, DreamPark.API.ProfileAchievement>>(L, 2)) 
                {
                    string _achievementId = LuaAPI.lua_tostring(L, 1);
                    System.Action<bool, DreamPark.API.ProfileAchievement> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.ProfileAchievement>>(L, 2);
                    
                    DreamPark.API.ProfileAPI.CompleteAchievement( _achievementId, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _achievementId = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.API.ProfileAPI.CompleteAchievement( _achievementId );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.CompleteAchievement!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AwardBadge_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.Action<bool, DreamPark.API.ProfileBadge>>(L, 2)) 
                {
                    string _badgeId = LuaAPI.lua_tostring(L, 1);
                    System.Action<bool, DreamPark.API.ProfileBadge> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.ProfileBadge>>(L, 2);
                    
                    DreamPark.API.ProfileAPI.AwardBadge( _badgeId, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _badgeId = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.API.ProfileAPI.AwardBadge( _badgeId );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.AwardBadge!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RemoveItem_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)&& translator.Assignable<System.Action<bool>>(L, 3)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    int _amount = LuaAPI.xlua_tointeger(L, 2);
                    System.Action<bool> _done = translator.GetDelegate<System.Action<bool>>(L, 3);
                    
                    DreamPark.API.ProfileAPI.RemoveItem( _itemId, _amount, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    int _amount = LuaAPI.xlua_tointeger(L, 2);
                    
                    DreamPark.API.ProfileAPI.RemoveItem( _itemId, _amount );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _itemId = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.API.ProfileAPI.RemoveItem( _itemId );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.RemoveItem!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RemoveBadge_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.Action<bool>>(L, 2)) 
                {
                    string _badgeId = LuaAPI.lua_tostring(L, 1);
                    System.Action<bool> _done = translator.GetDelegate<System.Action<bool>>(L, 2);
                    
                    DreamPark.API.ProfileAPI.RemoveBadge( _badgeId, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _badgeId = LuaAPI.lua_tostring(L, 1);
                    
                    DreamPark.API.ProfileAPI.RemoveBadge( _badgeId );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.RemoveBadge!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SendSessionHeartbeat_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 7&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.DateTime>(L, 2)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& translator.Assignable<System.Collections.Generic.Dictionary<string, float>>(L, 4)&& (LuaAPI.lua_isnil(L, 5) || LuaAPI.lua_type(L, 5) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 6)&& translator.Assignable<System.Action<bool>>(L, 7)) 
                {
                    string _sessionId = LuaAPI.lua_tostring(L, 1);
                    System.DateTime _startedAtUtc;translator.Get(L, 2, out _startedAtUtc);
                    float _durationSeconds = (float)LuaAPI.lua_tonumber(L, 3);
                    System.Collections.Generic.Dictionary<string, float> _contentTimes = (System.Collections.Generic.Dictionary<string, float>)translator.GetObject(L, 4, typeof(System.Collections.Generic.Dictionary<string, float>));
                    string _parkId = LuaAPI.lua_tostring(L, 5);
                    bool _ended = LuaAPI.lua_toboolean(L, 6);
                    System.Action<bool> _done = translator.GetDelegate<System.Action<bool>>(L, 7);
                    
                    DreamPark.API.ProfileAPI.SendSessionHeartbeat( _sessionId, _startedAtUtc, _durationSeconds, _contentTimes, _parkId, _ended, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 6&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.DateTime>(L, 2)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& translator.Assignable<System.Collections.Generic.Dictionary<string, float>>(L, 4)&& (LuaAPI.lua_isnil(L, 5) || LuaAPI.lua_type(L, 5) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 6)) 
                {
                    string _sessionId = LuaAPI.lua_tostring(L, 1);
                    System.DateTime _startedAtUtc;translator.Get(L, 2, out _startedAtUtc);
                    float _durationSeconds = (float)LuaAPI.lua_tonumber(L, 3);
                    System.Collections.Generic.Dictionary<string, float> _contentTimes = (System.Collections.Generic.Dictionary<string, float>)translator.GetObject(L, 4, typeof(System.Collections.Generic.Dictionary<string, float>));
                    string _parkId = LuaAPI.lua_tostring(L, 5);
                    bool _ended = LuaAPI.lua_toboolean(L, 6);
                    
                    DreamPark.API.ProfileAPI.SendSessionHeartbeat( _sessionId, _startedAtUtc, _durationSeconds, _contentTimes, _parkId, _ended );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 5&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.DateTime>(L, 2)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& translator.Assignable<System.Collections.Generic.Dictionary<string, float>>(L, 4)&& (LuaAPI.lua_isnil(L, 5) || LuaAPI.lua_type(L, 5) == LuaTypes.LUA_TSTRING)) 
                {
                    string _sessionId = LuaAPI.lua_tostring(L, 1);
                    System.DateTime _startedAtUtc;translator.Get(L, 2, out _startedAtUtc);
                    float _durationSeconds = (float)LuaAPI.lua_tonumber(L, 3);
                    System.Collections.Generic.Dictionary<string, float> _contentTimes = (System.Collections.Generic.Dictionary<string, float>)translator.GetObject(L, 4, typeof(System.Collections.Generic.Dictionary<string, float>));
                    string _parkId = LuaAPI.lua_tostring(L, 5);
                    
                    DreamPark.API.ProfileAPI.SendSessionHeartbeat( _sessionId, _startedAtUtc, _durationSeconds, _contentTimes, _parkId );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.DateTime>(L, 2)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& translator.Assignable<System.Collections.Generic.Dictionary<string, float>>(L, 4)) 
                {
                    string _sessionId = LuaAPI.lua_tostring(L, 1);
                    System.DateTime _startedAtUtc;translator.Get(L, 2, out _startedAtUtc);
                    float _durationSeconds = (float)LuaAPI.lua_tonumber(L, 3);
                    System.Collections.Generic.Dictionary<string, float> _contentTimes = (System.Collections.Generic.Dictionary<string, float>)translator.GetObject(L, 4, typeof(System.Collections.Generic.Dictionary<string, float>));
                    
                    DreamPark.API.ProfileAPI.SendSessionHeartbeat( _sessionId, _startedAtUtc, _durationSeconds, _contentTimes );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.SendSessionHeartbeat!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AddDreamPoints_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.Action<bool, int>>(L, 3)) 
                {
                    int _amount = LuaAPI.xlua_tointeger(L, 1);
                    string _reason = LuaAPI.lua_tostring(L, 2);
                    System.Action<bool, int> _done = translator.GetDelegate<System.Action<bool, int>>(L, 3);
                    
                    DreamPark.API.ProfileAPI.AddDreamPoints( _amount, _reason, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    int _amount = LuaAPI.xlua_tointeger(L, 1);
                    string _reason = LuaAPI.lua_tostring(L, 2);
                    
                    DreamPark.API.ProfileAPI.AddDreamPoints( _amount, _reason );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 1)) 
                {
                    int _amount = LuaAPI.xlua_tointeger(L, 1);
                    
                    DreamPark.API.ProfileAPI.AddDreamPoints( _amount );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.AddDreamPoints!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SpendDreamPoints_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& translator.Assignable<System.Action<bool, int>>(L, 3)) 
                {
                    int _amount = LuaAPI.xlua_tointeger(L, 1);
                    string _reason = LuaAPI.lua_tostring(L, 2);
                    System.Action<bool, int> _done = translator.GetDelegate<System.Action<bool, int>>(L, 3);
                    
                    DreamPark.API.ProfileAPI.SpendDreamPoints( _amount, _reason, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    int _amount = LuaAPI.xlua_tointeger(L, 1);
                    string _reason = LuaAPI.lua_tostring(L, 2);
                    
                    DreamPark.API.ProfileAPI.SpendDreamPoints( _amount, _reason );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 1&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 1)) 
                {
                    int _amount = LuaAPI.xlua_tointeger(L, 1);
                    
                    DreamPark.API.ProfileAPI.SpendDreamPoints( _amount );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.SpendDreamPoints!");
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_BoundUserId(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushstring(L, DreamPark.API.ProfileAPI.BoundUserId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_BoundDreamId(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushstring(L, DreamPark.API.ProfileAPI.BoundDreamId);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_ContentFilter(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushstring(L, DreamPark.API.ProfileAPI.ContentFilter);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsBound(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, DreamPark.API.ProfileAPI.IsBound);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsLoaded(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, DreamPark.API.ProfileAPI.IsLoaded);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_DisplayName(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushstring(L, DreamPark.API.ProfileAPI.DisplayName);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_AvatarUrl(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushstring(L, DreamPark.API.ProfileAPI.AvatarUrl);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_IsAnonymous(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, DreamPark.API.ProfileAPI.IsAnonymous);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_DreamPoints(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.xlua_pushinteger(L, DreamPark.API.ProfileAPI.DreamPoints);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Source(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.Push(L, DreamPark.API.ProfileAPI.Source);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Items(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.PushAny(L, DreamPark.API.ProfileAPI.Items);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Achievements(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.PushAny(L, DreamPark.API.ProfileAPI.Achievements);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Badges(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.PushAny(L, DreamPark.API.ProfileAPI.Badges);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
		
		
		
		
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnIdentityBound(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action gen_delegate = translator.GetDelegate<System.Action>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.ProfileAPI.OnIdentityBound += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.ProfileAPI.OnIdentityBound -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.OnIdentityBound!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnIdentityCleared(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action gen_delegate = translator.GetDelegate<System.Action>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.ProfileAPI.OnIdentityCleared += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.ProfileAPI.OnIdentityCleared -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.OnIdentityCleared!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnProfileLoaded(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action gen_delegate = translator.GetDelegate<System.Action>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.ProfileAPI.OnProfileLoaded += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.ProfileAPI.OnProfileLoaded -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.OnProfileLoaded!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnInventoryChanged(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action gen_delegate = translator.GetDelegate<System.Action>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.ProfileAPI.OnInventoryChanged += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.ProfileAPI.OnInventoryChanged -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.OnInventoryChanged!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnAchievementUpdated(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<DreamPark.API.ProfileAchievement> gen_delegate = translator.GetDelegate<System.Action<DreamPark.API.ProfileAchievement>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<DreamPark.API.ProfileAchievement>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.ProfileAPI.OnAchievementUpdated += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.ProfileAPI.OnAchievementUpdated -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.OnAchievementUpdated!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnBadgeAwarded(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<DreamPark.API.ProfileBadge> gen_delegate = translator.GetDelegate<System.Action<DreamPark.API.ProfileBadge>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<DreamPark.API.ProfileBadge>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.ProfileAPI.OnBadgeAwarded += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.ProfileAPI.OnBadgeAwarded -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.OnBadgeAwarded!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnItemAwarded(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<DreamPark.API.ProfileItem> gen_delegate = translator.GetDelegate<System.Action<DreamPark.API.ProfileItem>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<DreamPark.API.ProfileItem>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.ProfileAPI.OnItemAwarded += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.ProfileAPI.OnItemAwarded -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.OnItemAwarded!");
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _e_OnDreamPointsChanged(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    int gen_param_count = LuaAPI.lua_gettop(L);
                System.Action<int, int> gen_delegate = translator.GetDelegate<System.Action<int, int>>(L, 2);
                if (gen_delegate == null) {
                    return LuaAPI.luaL_error(L, "#2 need System.Action<int, int>!");
                }
                
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "+")) {
					DreamPark.API.ProfileAPI.OnDreamPointsChanged += gen_delegate;
					return 0;
				} 
				
				
				if (gen_param_count == 2 && LuaAPI.xlua_is_eq_str(L, 1, "-")) {
					DreamPark.API.ProfileAPI.OnDreamPointsChanged -= gen_delegate;
					return 0;
				} 
				
			} catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
			return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ProfileAPI.OnDreamPointsChanged!");
        }
        
    }
}
