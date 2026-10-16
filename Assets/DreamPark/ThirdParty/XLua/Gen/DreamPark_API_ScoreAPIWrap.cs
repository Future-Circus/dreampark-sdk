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
    public class DreamParkAPIScoreAPIWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(DreamPark.API.ScoreAPI);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 4, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "SubmitScore", _m_SubmitScore_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FetchMyScore", _m_FetchMyScore_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FetchLeaderboard", _m_FetchLeaderboard_xlua_st_);
            
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "DreamPark.API.ScoreAPI does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SubmitScore_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 6&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& (LuaAPI.lua_isnil(L, 4) || LuaAPI.lua_type(L, 4) == LuaTypes.LUA_TSTRING)&& translator.Assignable<DreamPark.API.ScoreDirection>(L, 5)&& translator.Assignable<System.Action<bool, DreamPark.API.ScoreSubmitResult>>(L, 6)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    double _value = LuaAPI.lua_tonumber(L, 3);
                    string _attraction = LuaAPI.lua_tostring(L, 4);
                    DreamPark.API.ScoreDirection _direction;translator.Get(L, 5, out _direction);
                    System.Action<bool, DreamPark.API.ScoreSubmitResult> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.ScoreSubmitResult>>(L, 6);
                    
                    DreamPark.API.ScoreAPI.SubmitScore( _contentId, _metric, _value, _attraction, _direction, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 5&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& (LuaAPI.lua_isnil(L, 4) || LuaAPI.lua_type(L, 4) == LuaTypes.LUA_TSTRING)&& translator.Assignable<DreamPark.API.ScoreDirection>(L, 5)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    double _value = LuaAPI.lua_tonumber(L, 3);
                    string _attraction = LuaAPI.lua_tostring(L, 4);
                    DreamPark.API.ScoreDirection _direction;translator.Get(L, 5, out _direction);
                    
                    DreamPark.API.ScoreAPI.SubmitScore( _contentId, _metric, _value, _attraction, _direction );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& (LuaAPI.lua_isnil(L, 4) || LuaAPI.lua_type(L, 4) == LuaTypes.LUA_TSTRING)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    double _value = LuaAPI.lua_tonumber(L, 3);
                    string _attraction = LuaAPI.lua_tostring(L, 4);
                    
                    DreamPark.API.ScoreAPI.SubmitScore( _contentId, _metric, _value, _attraction );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    double _value = LuaAPI.lua_tonumber(L, 3);
                    
                    DreamPark.API.ScoreAPI.SubmitScore( _contentId, _metric, _value );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ScoreAPI.SubmitScore!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FetchMyScore_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 6&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)&& translator.Assignable<DreamPark.API.LeaderboardWindow>(L, 5)&& translator.Assignable<System.Action<bool, DreamPark.API.MyScoreResult>>(L, 6)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    DreamPark.API.LeaderboardWindow _window;translator.Get(L, 5, out _window);
                    System.Action<bool, DreamPark.API.MyScoreResult> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.MyScoreResult>>(L, 6);
                    
                    DreamPark.API.ScoreAPI.FetchMyScore( _contentId, _metric, _attraction, _thisParkOnly, _window, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 5&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)&& translator.Assignable<DreamPark.API.LeaderboardWindow>(L, 5)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    DreamPark.API.LeaderboardWindow _window;translator.Get(L, 5, out _window);
                    
                    DreamPark.API.ScoreAPI.FetchMyScore( _contentId, _metric, _attraction, _thisParkOnly, _window );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    
                    DreamPark.API.ScoreAPI.FetchMyScore( _contentId, _metric, _attraction, _thisParkOnly );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    
                    DreamPark.API.ScoreAPI.FetchMyScore( _contentId, _metric, _attraction );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    
                    DreamPark.API.ScoreAPI.FetchMyScore( _contentId, _metric );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ScoreAPI.FetchMyScore!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FetchLeaderboard_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 10&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 5)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 6)&& (LuaAPI.lua_isnil(L, 7) || LuaAPI.lua_type(L, 7) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 8)&& translator.Assignable<DreamPark.API.LeaderboardWindow>(L, 9)&& translator.Assignable<System.Action<bool, DreamPark.API.LeaderboardResult>>(L, 10)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    bool _friendsOnly = LuaAPI.lua_toboolean(L, 5);
                    int _limit = LuaAPI.xlua_tointeger(L, 6);
                    string _cursor = LuaAPI.lua_tostring(L, 7);
                    bool _includeMe = LuaAPI.lua_toboolean(L, 8);
                    DreamPark.API.LeaderboardWindow _window;translator.Get(L, 9, out _window);
                    System.Action<bool, DreamPark.API.LeaderboardResult> _done = translator.GetDelegate<System.Action<bool, DreamPark.API.LeaderboardResult>>(L, 10);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric, _attraction, _thisParkOnly, _friendsOnly, _limit, _cursor, _includeMe, _window, _done );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 9&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 5)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 6)&& (LuaAPI.lua_isnil(L, 7) || LuaAPI.lua_type(L, 7) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 8)&& translator.Assignable<DreamPark.API.LeaderboardWindow>(L, 9)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    bool _friendsOnly = LuaAPI.lua_toboolean(L, 5);
                    int _limit = LuaAPI.xlua_tointeger(L, 6);
                    string _cursor = LuaAPI.lua_tostring(L, 7);
                    bool _includeMe = LuaAPI.lua_toboolean(L, 8);
                    DreamPark.API.LeaderboardWindow _window;translator.Get(L, 9, out _window);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric, _attraction, _thisParkOnly, _friendsOnly, _limit, _cursor, _includeMe, _window );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 8&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 5)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 6)&& (LuaAPI.lua_isnil(L, 7) || LuaAPI.lua_type(L, 7) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 8)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    bool _friendsOnly = LuaAPI.lua_toboolean(L, 5);
                    int _limit = LuaAPI.xlua_tointeger(L, 6);
                    string _cursor = LuaAPI.lua_tostring(L, 7);
                    bool _includeMe = LuaAPI.lua_toboolean(L, 8);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric, _attraction, _thisParkOnly, _friendsOnly, _limit, _cursor, _includeMe );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 7&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 5)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 6)&& (LuaAPI.lua_isnil(L, 7) || LuaAPI.lua_type(L, 7) == LuaTypes.LUA_TSTRING)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    bool _friendsOnly = LuaAPI.lua_toboolean(L, 5);
                    int _limit = LuaAPI.xlua_tointeger(L, 6);
                    string _cursor = LuaAPI.lua_tostring(L, 7);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric, _attraction, _thisParkOnly, _friendsOnly, _limit, _cursor );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 6&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 5)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 6)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    bool _friendsOnly = LuaAPI.lua_toboolean(L, 5);
                    int _limit = LuaAPI.xlua_tointeger(L, 6);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric, _attraction, _thisParkOnly, _friendsOnly, _limit );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 5&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 5)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    bool _friendsOnly = LuaAPI.lua_toboolean(L, 5);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric, _attraction, _thisParkOnly, _friendsOnly );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 4&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 4)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    bool _thisParkOnly = LuaAPI.lua_toboolean(L, 4);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric, _attraction, _thisParkOnly );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 3&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    string _attraction = LuaAPI.lua_tostring(L, 3);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric, _attraction );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    string _contentId = LuaAPI.lua_tostring(L, 1);
                    string _metric = LuaAPI.lua_tostring(L, 2);
                    
                    DreamPark.API.ScoreAPI.FetchLeaderboard( _contentId, _metric );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to DreamPark.API.ScoreAPI.FetchLeaderboard!");
            
        }
        
        
        
        
        
        
		
		
		
		
    }
}
