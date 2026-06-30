// ─────────────────────────────────────────────────────────────────────────
//  ProfileAPI.cs
//
//  DreamPark SDK — User Profile / Inventory / Achievements / Badges
//
//  Cross-game profile data API. Backed by /app/profile/* on the backend.
//  Identity is bound by the QR pairing flow in core (see
//  Assets/Scripts/ProfilePairingHandler.cs in dreampark-core) and cleared
//  automatically on session-end / sleep / cycle.
//
//  All gameplay scripts (C# or Lua) should hit this class — never the raw
//  HTTP endpoints — so that responses are cached, requests don't duplicate,
//  and behavior degrades gracefully when no user is bound (SDK preview /
//  guest play).
//
//  Calling pattern (C#):
//      ProfileAPI.FetchProfile("dragon-raid", (ok, profile) => { ... });
//      var wand = ProfileAPI.GetItemByType("wand");          // sync cache read
//      ProfileAPI.AwardItem("rune_fire", 1);                 // fire and forget
//
//  Calling pattern (Lua):
//      dp.profile.onReady(function()
//          local wand = dp.profile.getItemByType("wand")
//          if wand then ... end
//      end)
// ─────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;
using UnityEngine;
using XLua;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

namespace DreamPark.API
{
    /// <summary>
    /// Catalog + entry data for one inventory item, achievement, or badge.
    /// Fields are merged from the user's instance + the catalog doc.
    /// </summary>
    [System.Serializable]
    public class ProfileItem
    {
        public string itemId;        // base item id ("wand", "rune_fire")
        public string instanceId;    // unique key in inventory map (often itemId + suffix)
        public string baseItemId;    // base id stripped of suffix
        public string name;
        public string type;          // catalog "type" field — used for GetItemByType
        public string contentId;     // game/content scoping ("dragon-raid", "wizardsway-onboarding")
        public string gameId;        // legacy synonym for contentId
        public string rarity;
        public string iconUri;
        public string modelUri;
        public int amount = 1;
        public JSONObject metadata;  // free-form per-instance data
        public JSONObject raw;       // original JSON for advanced consumers
    }

    [System.Serializable]
    public class ProfileAchievement
    {
        public string achievementId;
        public float progress;
        public bool completed;
        public float maxValue;
        public string name;
        public string contentId;
        public JSONObject raw;
    }

    [System.Serializable]
    public class ProfileBadge
    {
        public string badgeId;
        public string name;
        public string contentId;
        public string iconUri;
        public string description;
        public double awardedAtMs;
        public JSONObject raw;
    }

    [LuaCallCSharp]
    public static class ProfileAPI
    {
        // ── Identity ─────────────────────────────────────────────────────
        public static string BoundUserId      { get; private set; }
        public static string BoundDreamId { get; private set; }
        public static string ContentFilter    { get; private set; }
        public static bool   IsBound          => !string.IsNullOrEmpty(BoundUserId) || !string.IsNullOrEmpty(BoundDreamId);
        public static bool   IsLoaded         { get; private set; }

        // ── Display identity (from the snapshot's `profile` block) ──────────
        // The bound account's displayName / avatarUrl / email. Null for an
        // unclaimed anonymous DreamID (IsAnonymous = true). Hydrated on every
        // profile load, cleared on ClearIdentity. This is what UI like the
        // DreamBand reads — NOT AuthAPI (which is the separate session path).
        public static string DisplayName { get; private set; }
        public static string AvatarUrl   { get; private set; }
        public static bool   IsAnonymous { get; private set; }

        // Player wallet — populated from the snapshot's `dreamPoints` field
        // when bound to a user account. Always 0 for anonymous DreamID
        // bindings (the backend doesn't issue points to unclaimed identities).
        public static int    DreamPoints      { get; private set; }

        // Server-issued preview key returned by /api/pairing/preview-claim
        // when BindToLoggedInUser succeeds. Sent as `Bearer <key>` on every
        // /app/profile/* write in SDK preview mode. Scoped server-side to
        // this developer's own uid — a leaked preview key can't touch any
        // other user's profile. Null in production (headset) builds, which
        // authenticate with the real Unity API key instead.
        static string _previewKey;

        // Which backend route family to hit. Headset = /app/profile/* with
        // the Unity API key + server-side headset binding (production path
        // on the Quest at a park). LoggedInUser = /app/profile/* with a
        // server-issued preview key (SDK preview / editor testing against
        // the developer's own account without needing to scan a QR).
        public enum ProfileSource { Headset, LoggedInUser }
        public static ProfileSource Source { get; private set; } = ProfileSource.Headset;

        // ── Cache ────────────────────────────────────────────────────────
        static readonly List<ProfileItem>        _items        = new List<ProfileItem>();
        static readonly List<ProfileAchievement> _achievements = new List<ProfileAchievement>();
        static readonly List<ProfileBadge>       _badges       = new List<ProfileBadge>();

        // ── Events ───────────────────────────────────────────────────────
        public static event Action OnIdentityBound;
        public static event Action OnIdentityCleared;
        public static event Action OnProfileLoaded;
        public static event Action OnInventoryChanged;
        public static event Action<ProfileAchievement> OnAchievementUpdated;
        public static event Action<ProfileBadge> OnBadgeAwarded;
        // Fired whenever DreamPoints changes — including hydrate from snapshot,
        // earn from /add, and debit from /spend. Subscribe to drive HUD updates.
        public static event Action<int /* newBalance */, int /* delta */> OnDreamPointsChanged;

        // ── Pending callbacks (Lua scripts can call .onReady before data lands) ─
        static readonly List<Action> _onReadyQueue = new List<Action>();

        /// <summary>Bind an identity (called by the core QR pairing handler).
        /// Sets Source = Headset — all reads/writes go through /app/profile/*
        /// which the backend authorizes by looking up the headset's binding.</summary>
        public static void BindIdentity(string userId, string dreamId, string contentFilter = null, JSONObject initialSnapshot = null)
        {
            Source           = ProfileSource.Headset;
            BoundUserId      = string.IsNullOrEmpty(userId) ? null : userId;
            BoundDreamId = string.IsNullOrEmpty(dreamId) ? null : dreamId;
            ContentFilter    = string.IsNullOrEmpty(contentFilter) ? null : contentFilter;
            IsLoaded         = false;
            try { OnIdentityBound?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnIdentityBound subscriber threw: {e}"); }

            // If the caller already holds the authoritative snapshot (the
            // /app/profile/claim response carries identity + profile + inventory),
            // apply it DIRECTLY. It's bound to the userId we just paired and always
            // includes displayName. A second GET /app/profile re-resolves the binding
            // server-side from the headset doc, which can land on the anonymous
            // DreamID (avatar but NO displayName) — exactly the "name shows Player"
            // bug. Fetch only when we have no snapshot (restore/boot paths).
            if (initialSnapshot != null)
            {
                ApplyProfileSnapshot(HydrateProfileSnapshot(initialSnapshot), null);
            }
            else
            {
                FetchProfile(ContentFilter, null);
            }
        }

        /// <summary>
        /// SDK preview / editor testing: exercise the REAL pairing-token
        /// pipeline against your own logged-in account — no QR scan needed.
        ///
        /// Flow:
        ///   1. POST /api/pairing/create  (session bearer) → server mints
        ///      a token tied to your uid, returns { token, url, expiresAt }.
        ///   2. POST /api/pairing/preview-claim  (session bearer) → server
        ///      validates the token belongs to this session, marks it
        ///      redeemed, returns the same profile snapshot /app/profile/claim
        ///      would have returned to a headset.
        ///   3. ApplyProfileSnapshot — exactly the production code path runs.
        ///
        /// This means dp.profile.* in editor exercises the same write
        /// paths (/api/user/inventory/add etc. for the LoggedInUser source),
        /// the same hydration, the same token validation, the same caching.
        /// Bugs in the pairing token machinery surface in editor.
        ///
        /// Requires AuthAPI.isLoggedIn — sign in via the SDK login first.
        /// </summary>
        public static void BindToLoggedInUser(string contentFilter = null)
        {
            if (!AuthAPI.isLoggedIn || string.IsNullOrEmpty(AuthAPI.userId))
            {
                Debug.LogWarning("[ProfileAPI] BindToLoggedInUser — no session. Log in via the SDK first.");
                return;
            }
            Source           = ProfileSource.LoggedInUser;
            BoundUserId      = AuthAPI.userId;
            BoundDreamId = null;
            ContentFilter    = string.IsNullOrEmpty(contentFilter) ? null : contentFilter;
            IsLoaded         = false;
            Debug.Log($"[ProfileAPI] BindToLoggedInUser — minting pairing token for uid {BoundUserId}…");
            try { OnIdentityBound?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnIdentityBound subscriber threw: {e}"); }

            // Step 1: mint a pairing token via the same endpoint the web
            // profile page uses for QR codes. CSRF guard requires the
            // X-Requested-With header — but DreamParkAPI doesn't expose
            // arbitrary headers, so we pass a stub body and rely on the
            // session bearer auth path.
            // Defective.JSON's `new JSONObject()` defaults to type=Null —
            // serializes to "null". The server's body parser doesn't like
            // that, so we explicitly create an empty object {}.
            DreamParkAPI.POST("/api/pairing/create", AuthAPI.GetUserAuth(), new JSONObject(JSONObject.Type.Object), (ok, resp) =>
            {
                if (!ok || resp?.json == null)
                {
                    Debug.LogWarning("[ProfileAPI] BindToLoggedInUser: /api/pairing/create failed: " + resp?.error);
                    FlushReadyQueue(failed: true);
                    return;
                }
                var token = resp.json.GetField("token")?.stringValue;
                if (string.IsNullOrEmpty(token))
                {
                    Debug.LogWarning("[ProfileAPI] BindToLoggedInUser: no token in /api/pairing/create response");
                    FlushReadyQueue(failed: true);
                    return;
                }

                // Step 2: redeem it via the preview-claim endpoint.
                var claimBody = new JSONObject();
                claimBody.AddField("token", token);
                if (!string.IsNullOrEmpty(ContentFilter)) claimBody.AddField("contentId", ContentFilter);

                DreamParkAPI.POST("/api/pairing/preview-claim", AuthAPI.GetUserAuth(), claimBody, (claimOk, claimResp) =>
                {
                    if (!claimOk || claimResp?.json == null)
                    {
                        Debug.LogWarning("[ProfileAPI] BindToLoggedInUser: /api/pairing/preview-claim failed: " + claimResp?.error);
                        FlushReadyQueue(failed: true);
                        return;
                    }
                    // Extract the server-issued preview key — subsequent
                    // writes use it as `Bearer <key>` on /app/profile/*.
                    // Without it, AwardItem/AwardBadge/AwardAchievement
                    // would 401 since SDK preview can no longer fall back
                    // to plain session bearer (that surface was retired
                    // because it let any authed user grant arbitrary items).
                    var previewKeyObj = claimResp.json.GetField("previewKey");
                    _previewKey = previewKeyObj?.GetField("key")?.stringValue;
                    if (string.IsNullOrEmpty(_previewKey))
                    {
                        Debug.LogWarning("[ProfileAPI] BindToLoggedInUser: preview-claim returned no previewKey — writes will fail.");
                    }
                    Debug.Log("[ProfileAPI] BindToLoggedInUser: preview-claim succeeded, applying profile snapshot.");
                    ApplyProfileSnapshot(HydrateProfileSnapshot(claimResp.json), null);
                });
            });
        }

        public static void ClearIdentity()
        {
            BoundUserId = null;
            BoundDreamId = null;
            _previewKey = null;
            DreamPoints = 0;
            DisplayName = null;
            AvatarUrl   = null;
            IsAnonymous = false;
            IsLoaded = false;
            _items.Clear();
            _achievements.Clear();
            _badges.Clear();
            try { OnIdentityCleared?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnIdentityCleared subscriber threw: {e}"); }
        }

        static string AuthHeader()
        {
#if DREAMPARKCORE
            // Production headset path — the embedded Unity API key. Server
            // resolves identity from the headset binding on the active
            // consumer_session, so writes go to the paired user/DreamID.
            return AuthAPI.GetAPIKey();
#else
            // SDK preview path — the server-issued preview key minted by
            // /api/pairing/preview-claim. Server resolves identity to the
            // key's bound uid (the developer's own). Writes are server-side
            // scoped to that uid only; a leaked preview key cannot mutate
            // any other user's profile.
            return string.IsNullOrEmpty(_previewKey) ? "" : $"Bearer {_previewKey}";
#endif
        }

        /// <summary>"user:abc" or "did:XYZ" or null — used only for display
        /// and diagnostics; the server never trusts this string.</summary>
        public static string IdentitySegment()
        {
            if (!string.IsNullOrEmpty(BoundUserId)) return "user:" + BoundUserId;
            if (!string.IsNullOrEmpty(BoundDreamId)) return "did:" + BoundDreamId;
            return null;
        }

        // Headset id flows in the X-Headset-Id header; the backend looks up
        // which identity this headset is bound to and never trusts a client-
        // supplied uid. SDK preview builds (no real headset) fall back to a
        // stable per-install id so things still work in the editor.
        static string HeadsetIdHeader()
        {
            return SystemInfo.deviceUniqueIdentifier;
        }

        // ── Fetch ────────────────────────────────────────────────────────
        public static void FetchProfile(string contentFilter, Action<bool, ProfileSnapshot> done)
        {
            if (!IsBound)
            {
                // No identity bound yet (no QR scan, no manual BindIdentity).
                // Do NOT flush the onReady queue — callbacks are waiting for
                // "we have data," not "we tried and got nothing." Bindings
                // can happen 1 second or 30 minutes after a script registers
                // onReady, and we want those callbacks to still fire when
                // BindIdentity eventually triggers a real fetch.
                Debug.Log("[ProfileAPI] FetchProfile skipped — no identity bound yet. Callbacks remain queued.");
                done?.Invoke(false, null);
                return;
            }

            if (!string.IsNullOrEmpty(contentFilter)) ContentFilter = contentFilter;

            if (Source == ProfileSource.LoggedInUser)
            {
                FetchProfileForLoggedInUser(done);
                return;
            }

            // Headset path — /app/profile/* with API key, backend looks up binding
            var qs = "?headsetId=" + UnityWebRequestEscape(HeadsetIdHeader());
            if (!string.IsNullOrEmpty(ContentFilter)) qs += "&contentId=" + UnityWebRequestEscape(ContentFilter);
            var url = "/app/profile" + qs;

            DreamParkAPI.GET(url, AuthHeader(), (success, response) =>
            {
                if (!success || response?.json == null)
                {
                    Debug.LogWarning($"[ProfileAPI] FetchProfile failed: {response?.error}");
                    FlushReadyQueue(failed: true);
                    done?.Invoke(false, null);
                    return;
                }
                ApplyProfileSnapshot(HydrateProfileSnapshot(response.json), done);
            });
        }

        // Shared apply-and-fire — used by both fetch paths.
        static void ApplyProfileSnapshot(ProfileSnapshot snapshot, Action<bool, ProfileSnapshot> done)
        {
            _items.Clear();        _items.AddRange(snapshot.items);
            _achievements.Clear(); _achievements.AddRange(snapshot.achievements);
            _badges.Clear();       _badges.AddRange(snapshot.badges);
            int prevPoints = DreamPoints;
            DreamPoints = snapshot.dreamPoints;

            // Identity guard: a later fetch can resolve to the anonymous DreamID
            // (which has an avatar but NO displayName/handle/email). Applying it
            // unconditionally would wipe the real signed-in identity we already
            // loaded — the "DreamBand reverts to Player" bug. So skip the identity
            // overwrite when the incoming snapshot is anonymous AND we already hold
            // a real display identity. (Inventory / points / badges still update.)
            bool haveRealIdentity = !string.IsNullOrEmpty(DisplayName);
            if (!(snapshot.isAnonymous && haveRealIdentity))
            {
                DisplayName = snapshot.displayName;
                AvatarUrl   = snapshot.avatarUrl;
                IsAnonymous = snapshot.isAnonymous;
            }
            else
            {
                Debug.Log($"[ProfileAPI] Ignored anonymous snapshot — kept identity displayName='{DisplayName}'.");
            }
            IsLoaded = true;
            try { OnProfileLoaded?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnProfileLoaded subscriber threw: {e}"); }
            if (prevPoints != DreamPoints) {
                try { OnDreamPointsChanged?.Invoke(DreamPoints, DreamPoints - prevPoints); } catch (Exception e) { Debug.LogWarning(e); }
            }
            FlushReadyQueue(failed: false);
            done?.Invoke(true, snapshot);
        }

        // LoggedInUser refresh path — hits the session-authed snapshot
        // endpoint that returns the exact same shape /app/profile would.
        // Used after AwardItem / AwardBadge / AwardAchievement to refresh
        // the cache. The initial bind goes through the pairing-token flow
        // in BindToLoggedInUser (proves the token machinery works end-to-end);
        // subsequent reads use this lightweight single GET.
        static void FetchProfileForLoggedInUser(Action<bool, ProfileSnapshot> done)
        {
            var qs = string.IsNullOrEmpty(ContentFilter) ? "" : "?contentId=" + UnityWebRequestEscape(ContentFilter);
            DreamParkAPI.GET("/api/pairing/profile-self" + qs, AuthAPI.GetUserAuth(), (ok, resp) =>
            {
                if (!ok || resp?.json == null)
                {
                    Debug.LogWarning("[ProfileAPI] LoggedInUser fetch failed: " + resp?.error);
                    FlushReadyQueue(failed: true);
                    done?.Invoke(false, null);
                    return;
                }
                ApplyProfileSnapshot(HydrateProfileSnapshot(resp.json), done);
            });
        }

        static void FlushReadyQueue(bool failed = false)
        {
            if (_onReadyQueue.Count == 0) return;
            var snapshot = _onReadyQueue.ToArray();
            _onReadyQueue.Clear();
            foreach (var cb in snapshot)
            {
                try { cb?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] onReady callback threw: {e}"); }
            }
            if (failed) Debug.LogWarning("[ProfileAPI] Flushed onReady queue without data (fetch failed) — consumers will see empty cache.");
        }

        /// <summary>Register a callback that fires once profile data is loaded.
        /// If already loaded, fires immediately on this frame.</summary>
        public static void OnReady(Action callback)
        {
            if (callback == null) return;
            if (IsLoaded) { try { callback(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnReady subscriber threw: {e}"); } return; }
            _onReadyQueue.Add(callback);
        }

        // ── Reads (synchronous; return null if no data yet) ──────────────
        public static IReadOnlyList<ProfileItem>        Items        => _items;
        public static IReadOnlyList<ProfileAchievement> Achievements => _achievements;
        public static IReadOnlyList<ProfileBadge>       Badges       => _badges;

        public static ProfileItem GetItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.itemId == itemId || it.baseItemId == itemId || it.instanceId == itemId) return it;
            }
            return null;
        }

        public static ProfileItem GetItemByType(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            for (int i = 0; i < _items.Count; i++) if (_items[i].type == type) return _items[i];
            return null;
        }

        public static ProfileItem GetItemByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _items.Count; i++) if (_items[i].name == name) return _items[i];
            return null;
        }

        public static List<ProfileItem> GetItemsByType(string type)
        {
            var list = new List<ProfileItem>();
            if (string.IsNullOrEmpty(type)) return list;
            for (int i = 0; i < _items.Count; i++) if (_items[i].type == type) list.Add(_items[i]);
            return list;
        }

        public static bool HasItem(string itemId)       => GetItem(itemId) != null;
        public static bool HasItemByType(string type)   => GetItemByType(type) != null;
        public static bool HasBadge(string badgeId)     => GetBadge(badgeId) != null;
        public static bool HasAchievement(string id)    => GetAchievement(id) != null;

        public static ProfileAchievement GetAchievement(string achievementId)
        {
            if (string.IsNullOrEmpty(achievementId)) return null;
            for (int i = 0; i < _achievements.Count; i++) if (_achievements[i].achievementId == achievementId) return _achievements[i];
            return null;
        }

        public static ProfileBadge GetBadge(string badgeId)
        {
            if (string.IsNullOrEmpty(badgeId)) return null;
            for (int i = 0; i < _badges.Count; i++) if (_badges[i].badgeId == badgeId) return _badges[i];
            return null;
        }

        // ── Writes (fire-and-forget; optimistic local update) ────────────
        // All writes go through /app/profile/* — the single locked-down
        // write surface for inventory/achievements/badges. The previous
        // /api/user/* alternative was removed because it accepted any
        // session bearer, letting any authed user grant themselves
        // arbitrary items. AuthHeader() picks the right credential for
        // the build (production = Unity API key; SDK preview = server-
        // issued preview key bound to the developer's own uid).
        static (string url, string auth) PickWrite(string suffix)
        {
            return ("/app/profile/" + suffix, AuthHeader());
        }

        public static void AwardItem(string itemId, int amount = 1, JSONObject metadata = null, Action<bool, ProfileItem> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] AwardItem with no identity bound."); done?.Invoke(false, null); return; }

            var body = new JSONObject();
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("itemId", itemId);
            body.AddField("amount", amount);
            if (metadata != null) body.AddField("metadata", metadata);

            var (url, auth) = PickWrite("inventory/add");
            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false, null); return; }
                FetchProfile(ContentFilter, (_, __) =>
                {
                    try { OnInventoryChanged?.Invoke(); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true, GetItem(itemId));
                });
            });
        }

        public static void AwardAchievement(string achievementId, float progress = 1, Action<bool, ProfileAchievement> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] AwardAchievement with no identity bound."); done?.Invoke(false, null); return; }

            var body = new JSONObject();
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("achievementId", achievementId);
            body.AddField("progress", progress);

            var (url, auth) = PickWrite("achievements/add");
            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false, null); return; }
                FetchProfile(ContentFilter, (_, __) =>
                {
                    var ach = GetAchievement(achievementId);
                    try { OnAchievementUpdated?.Invoke(ach); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true, ach);
                });
            });
        }

        public static void AwardBadge(string badgeId, Action<bool, ProfileBadge> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] AwardBadge with no identity bound."); done?.Invoke(false, null); return; }

            var body = new JSONObject();
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("badgeId", badgeId);

            var (url, auth) = PickWrite("badges/award");
            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false, null); return; }
                FetchProfile(ContentFilter, (_, __) =>
                {
                    var b = GetBadge(badgeId);
                    try { OnBadgeAwarded?.Invoke(b); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true, b);
                });
            });
        }

        /// <summary>
        /// Remove <paramref name="amount"/> copies of <paramref name="itemId"/>
        /// from the user's inventory. Decrements the stack; when the stored
        /// amount hits zero the entry is deleted.
        ///
        /// For unique (metadata-tagged) items, pass the full instance id
        /// (e.g. "wand_stormheart_a3f24b81") and amount=1 to remove that
        /// specific row. Server is idempotent — removing more than is owned
        /// just clears the entry.
        /// </summary>
        public static void RemoveItem(string itemId, int amount = 1, Action<bool> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] RemoveItem with no identity bound."); done?.Invoke(false); return; }
            if (string.IsNullOrEmpty(itemId)) { done?.Invoke(false); return; }
            if (amount <= 0) amount = 1;

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("itemId", itemId);
            body.AddField("amount", amount);

            var (url, auth) = PickWrite("inventory/remove");
            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false); return; }
                FetchProfile(ContentFilter, (_, __) =>
                {
                    try { OnInventoryChanged?.Invoke(); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true);
                });
            });
        }

        /// <summary>
        /// Strip a badge off the user. Idempotent — no-op if the user
        /// didn't own the badge.
        /// </summary>
        public static void RemoveBadge(string badgeId, Action<bool> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] RemoveBadge with no identity bound."); done?.Invoke(false); return; }
            if (string.IsNullOrEmpty(badgeId)) { done?.Invoke(false); return; }

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("badgeId", badgeId);

            var (url, auth) = PickWrite("badges/remove");

            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false); return; }
                FetchProfile(ContentFilter, (_, __) => done?.Invoke(true));
            });
        }

        /// <summary>
        /// Heartbeat the current play session — absolute totals, NOT deltas.
        /// The server reads the session doc, diffs old vs new, and credits
        /// only the difference. Repeating identical payloads is a no-op;
        /// dropped heartbeats are recovered on the next successful call.
        ///
        /// `sessionId` is a UUID minted by the SDK at park-load. `startedAt`
        /// is recorded at park-load too — so the first heartbeat after a
        /// late login credits ALL elapsed time (including the anonymous
        /// portion before the user paired).
        ///
        /// parkId is optional — when omitted, the backend uses the bound
        /// session's locationId.
        ///
        /// This is the LOW-LEVEL write. SessionReporter wraps it with
        /// automatic park-load detection + 60s ticker + zone tracking;
        /// most creators won't call this directly.
        /// </summary>
        public static void SendSessionHeartbeat(
            string sessionId,
            DateTime startedAtUtc,
            float durationSeconds,
            Dictionary<string, float> contentTimes,
            string parkId = null,
            bool ended = false,
            Action<bool> done = null)
        {
            if (!IsBound) { done?.Invoke(false); return; }
            if (string.IsNullOrEmpty(sessionId)) { Debug.LogWarning("[ProfileAPI] SessionHeartbeat empty sessionId."); done?.Invoke(false); return; }

            var body = new JSONObject();
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("sessionId", sessionId);
            body.AddField("startedAt", startedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            body.AddField("durationSeconds", durationSeconds);
            if (!string.IsNullOrEmpty(parkId)) body.AddField("parkId", parkId);

            // Per-content time map. Empty object is fine — server treats it
            // as "no per-game breakdown for this heartbeat" (still credits
            // session total to parkStats).
            var ct = new JSONObject(JSONObject.Type.Object);
            if (contentTimes != null)
            {
                foreach (var kv in contentTimes)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0f) continue;
                    ct.AddField(kv.Key, kv.Value);
                }
            }
            body.AddField("contentTimes", ct);

            var suffix = ended ? "session/end" : "session/heartbeat";
            var (url, auth) = PickWrite(suffix);
            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok) Debug.LogWarning($"[ProfileAPI] {suffix} failed.");
                done?.Invoke(ok);
            });
        }

        /// <summary>
        /// Award DreamPoints to the bound user. PRODUCTION HEADSET ONLY —
        /// the server rejects this call when authenticated with an SDK
        /// preview key (403, sdkPreviewBlocked: true). SDK builds can wire
        /// the call into their game logic, but minting only succeeds in
        /// real Quest builds where the embedded Unity API key is present.
        ///
        /// Anonymous DreamID bindings are also rejected server-side —
        /// DreamPoints live on the `users` collection only.
        ///
        /// `reason` is recorded on the ledger for audit (e.g. "quest_complete").
        /// </summary>
        public static void AddDreamPoints(int amount, string reason = null, Action<bool, int /* newBalance */> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] AddDreamPoints with no identity bound."); done?.Invoke(false, DreamPoints); return; }
            if (amount <= 0) { Debug.LogWarning("[ProfileAPI] AddDreamPoints requires a positive amount."); done?.Invoke(false, DreamPoints); return; }

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("amount", amount);
            if (!string.IsNullOrEmpty(reason)) body.AddField("reason", reason);

            var (url, auth) = PickWrite("dreampoints/add");
            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok)
                {
                    var err = resp?.error ?? "request failed";
                    Debug.LogWarning("[ProfileAPI] AddDreamPoints failed: " + err);
                    done?.Invoke(false, DreamPoints);
                    return;
                }
                int prev = DreamPoints;
                int newBalance = (int)(resp?.json?.GetField("dreamPoints")?.floatValue ?? prev + amount);
                DreamPoints = newBalance;
                try { OnDreamPointsChanged?.Invoke(newBalance, newBalance - prev); } catch (Exception e) { Debug.LogWarning(e); }
                done?.Invoke(true, newBalance);
            });
        }

        /// <summary>
        /// Spend DreamPoints from the bound user's balance. Server rejects on
        /// insufficient balance (HTTP 402). Works with both production and
        /// SDK preview auth — devs need to test purchase flows in editor.
        ///
        /// `reason` is recorded on the ledger (e.g. "shop:cosmetic_hat_red").
        /// </summary>
        public static void SpendDreamPoints(int amount, string reason = null, Action<bool, int /* newBalance */> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] SpendDreamPoints with no identity bound."); done?.Invoke(false, DreamPoints); return; }
            if (amount <= 0) { Debug.LogWarning("[ProfileAPI] SpendDreamPoints requires a positive amount."); done?.Invoke(false, DreamPoints); return; }

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("amount", amount);
            if (!string.IsNullOrEmpty(reason)) body.AddField("reason", reason);

            var (url, auth) = PickWrite("dreampoints/spend");
            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok)
                {
                    var err = resp?.error ?? "request failed";
                    Debug.LogWarning("[ProfileAPI] SpendDreamPoints failed: " + err);
                    done?.Invoke(false, DreamPoints);
                    return;
                }
                int prev = DreamPoints;
                int newBalance = (int)(resp?.json?.GetField("dreamPoints")?.floatValue ?? Math.Max(0, prev - amount));
                DreamPoints = newBalance;
                try { OnDreamPointsChanged?.Invoke(newBalance, newBalance - prev); } catch (Exception e) { Debug.LogWarning(e); }
                done?.Invoke(true, newBalance);
            });
        }

        // ── JSON hydration ───────────────────────────────────────────────
        public class ProfileSnapshot
        {
            public string identityKind;
            public string identityId;
            public string displayName;
            public string avatarUrl;
            public bool   isAnonymous;
            public int    dreamPoints;
            public List<ProfileItem>        items        = new List<ProfileItem>();
            public List<ProfileAchievement> achievements = new List<ProfileAchievement>();
            public List<ProfileBadge>       badges       = new List<ProfileBadge>();
        }

        static ProfileSnapshot HydrateProfileSnapshot(JSONObject json)
        {
            var b = new ProfileSnapshot();
            if (json == null) return b;

            var identity = json.GetField("identity");
            if (identity != null)
            {
                b.identityKind = identity.GetField("kind")?.stringValue;
                b.identityId   = identity.GetField("id")?.stringValue;
            }

            // The bound account's display identity (displayName/avatarUrl/email).
            // Backend buildProfileSnapshot returns this on /app/profile, /claim,
            // and /api/pairing/profile-self. Null fields = anonymous DreamID.
            var profile = json.GetField("profile");
            Debug.Log($"[ProfileAPI] hydrate (baseUrl={DreamParkAPI.baseUrl}) raw profile block = {(profile != null ? profile.Print() : "NULL")}");
            if (profile != null)
            {
                b.displayName = profile.GetField("displayName")?.stringValue;
                b.avatarUrl   = profile.GetField("avatarUrl")?.stringValue;
                b.isAnonymous = profile.GetField("isAnonymous")?.boolValue ?? (b.identityKind == "dreamid");
            }

            // DreamPoints balance — 0 for anonymous DreamID bindings.
            b.dreamPoints = (int)(json.GetField("dreamPoints")?.floatValue ?? 0);

            var inv = json.GetField("inventory");
            if (inv != null && inv.type == JSONObject.Type.Array && inv.list != null)
            {
                for (int i = 0; i < inv.list.Count; i++)
                {
                    var entry = inv.list[i];
                    var meta  = entry.GetField("metadata");

                    // Helper: prefer top-level catalog field, fall back to
                    // per-instance metadata. This is the key to making
                    // GetItemByType / GetItemByName work for items granted
                    // through ad-hoc flows (anonymous web onboarding, custom
                    // award calls) that put descriptive fields in metadata
                    // instead of in the items/{id} catalog doc.
                    string getStr(string topKey, string metaKey = null)
                    {
                        var top = entry.GetField(topKey)?.stringValue;
                        if (!string.IsNullOrEmpty(top)) return top;
                        if (meta != null) {
                            var m = meta.GetField(metaKey ?? topKey)?.stringValue;
                            if (!string.IsNullOrEmpty(m)) return m;
                        }
                        return null;
                    }

                    var it = new ProfileItem
                    {
                        itemId      = entry.GetField("itemId")?.stringValue,
                        instanceId  = entry.GetField("instanceId")?.stringValue,
                        baseItemId  = entry.GetField("baseItemId")?.stringValue,
                        name        = getStr("name"),
                        type        = getStr("type"),
                        contentId   = getStr("contentId"),
                        gameId      = entry.GetField("gameId")?.stringValue,
                        rarity      = getStr("rarity"),
                        iconUri     = getStr("iconUri") ?? entry.GetField("icon")?.stringValue,
                        modelUri    = getStr("modelUri"),
                        amount      = (int)(entry.GetField("amount")?.floatValue ?? 1),
                        metadata    = meta,
                        raw         = entry,
                    };
                    if (string.IsNullOrEmpty(it.contentId)) it.contentId = it.gameId;
                    b.items.Add(it);
                }
            }

            var achievements = json.GetField("achievements");
            if (achievements != null && achievements.type == JSONObject.Type.Array && achievements.list != null)
            {
                for (int i = 0; i < achievements.list.Count; i++)
                {
                    var entry = achievements.list[i];
                    b.achievements.Add(new ProfileAchievement
                    {
                        achievementId = entry.GetField("achievementId")?.stringValue,
                        progress      = entry.GetField("progress")?.floatValue ?? 0,
                        completed     = entry.GetField("completed")?.boolValue ?? false,
                        maxValue      = entry.GetField("maxValue")?.floatValue ?? 0,
                        name          = entry.GetField("name")?.stringValue,
                        contentId     = entry.GetField("contentId")?.stringValue,
                        raw           = entry,
                    });
                }
            }

            var badges = json.GetField("badges");
            if (badges != null && badges.type == JSONObject.Type.Array && badges.list != null)
            {
                for (int i = 0; i < badges.list.Count; i++)
                {
                    var entry = badges.list[i];
                    b.badges.Add(new ProfileBadge
                    {
                        badgeId      = entry.GetField("badgeId")?.stringValue,
                        name         = entry.GetField("name")?.stringValue,
                        contentId    = entry.GetField("contentId")?.stringValue,
                        iconUri      = entry.GetField("iconUri")?.stringValue,
                        description  = entry.GetField("description")?.stringValue,
                        awardedAtMs  = ParseTimestampMs(entry.GetField("awardedAt")),
                        raw          = entry,
                    });
                }
            }

            return b;
        }

        // ── Helpers ──────────────────────────────────────────────────────
        static string UnityWebRequestEscape(string s) =>
            System.Uri.EscapeDataString(s ?? "");

        // Tolerant Firestore timestamp parse — handles both shapes the backend
        // can produce:
        //   1. { _seconds: 12345, _nanoseconds: 0 } (Admin SDK toJSON default)
        //   2. "2026-05-27T10:00:00.000Z"           (ISO 8601 string)
        //   3. 1716800000000                        (raw epoch ms)
        // Returns 0 if the field is null/missing/unparseable.
        static double ParseTimestampMs(JSONObject t)
        {
            if (t == null || t.type == JSONObject.Type.Null) return 0;

            // Shape 1 — Firestore admin object
            if (t.type == JSONObject.Type.Object)
            {
                var seconds = t.GetField("_seconds")?.floatValue ?? 0;
                if (seconds > 0) return seconds * 1000;
            }
            // Shape 2 — ISO string
            if (t.type == JSONObject.Type.String && !string.IsNullOrEmpty(t.stringValue))
            {
                if (DateTime.TryParse(t.stringValue, null,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dt))
                {
                    return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                }
            }
            // Shape 3 — raw epoch number (seconds or ms — disambiguate)
            if (t.type == JSONObject.Type.Number)
            {
                var n = t.floatValue;
                return n > 10_000_000_000 ? n : n * 1000; // > ~Sat 2286-Nov-20 in seconds = surely ms
            }
            return 0;
        }

        // ── Lua bridge ───────────────────────────────────────────────────
        // Registered into LuaBehaviour.luaEnv.Global on the very first scene
        // load, before any LuaBehaviour's Awake fires. Lua scripts get a
        // global `dp` table:  dp.profile.getItemByType("wand"), etc.
        //
        // IMPORTANT — IL2CPP / Quest portability:
        // We intentionally do NOT return raw C# ProfileItem/Achievement/Badge
        // objects across the Lua boundary. Returning a custom C# type would
        // force every consumer to add [LuaCallCSharp] to those types and run
        // "XLua → Generate Code" before every release. Instead we convert
        // each object into a native LuaTable here. Lua scripts get a plain
        // table they can read with dot notation, no wrap generation needed.
        // ──────────────────────────────────────────────────────────────────
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RegisterLua()
        {
            try
            {
                var env = LuaBehaviour.GetLuaEnv();
                if (env == null) return;

                // ── primitive accessors ─────────────────────────────────
                env.Global.Set("dp_profile_is_loaded",     new Func<bool>(()                      => IsLoaded));
                env.Global.Set("dp_profile_is_bound",      new Func<bool>(()                      => IsBound));
                env.Global.Set("dp_profile_identity",      new Func<string>(()                    => IdentitySegment()));
                env.Global.Set("dp_profile_content_id",    new Func<string>(()                    => ContentFilter));
                env.Global.Set("dp_profile_on_ready",      new Action<Action>(cb                  => OnReady(cb)));
                env.Global.Set("dp_profile_refresh",       new Action(()                          => FetchProfile(ContentFilter, null)));

                // ── readers — return LuaTable (or nil), not C# types ────
                env.Global.Set("dp_profile_get_item",          new Func<string, LuaTable>(id => ItemToLuaTable(env, GetItem(id))));
                env.Global.Set("dp_profile_get_item_by_type",  new Func<string, LuaTable>(t  => ItemToLuaTable(env, GetItemByType(t))));
                env.Global.Set("dp_profile_get_item_by_name",  new Func<string, LuaTable>(n  => ItemToLuaTable(env, GetItemByName(n))));
                env.Global.Set("dp_profile_has_item",          new Func<string, bool>(HasItem));
                env.Global.Set("dp_profile_has_item_by_type",  new Func<string, bool>(HasItemByType));
                env.Global.Set("dp_profile_get_achievement",   new Func<string, LuaTable>(id => AchievementToLuaTable(env, GetAchievement(id))));
                env.Global.Set("dp_profile_has_achievement",   new Func<string, bool>(HasAchievement));
                env.Global.Set("dp_profile_get_badge",         new Func<string, LuaTable>(id => BadgeToLuaTable(env, GetBadge(id))));
                env.Global.Set("dp_profile_has_badge",         new Func<string, bool>(HasBadge));
                env.Global.Set("dp_profile_get_dreampoints",   new Func<int>(()                       => DreamPoints));

                // ── writers ─────────────────────────────────────────────
                // awardItem accepts a makeUnique bool — when true, we
                // send a stub metadata object so the backend appends a
                // random suffix to the inventory key and forces amount=1.
                // Any truthy `metadata` triggers the unique path server-side
                // (see /api/user/inventory/add + /app/profile/inventory/add).
                env.Global.Set("dp_profile_award_item",        new Action<string, int, bool>((id, amt, unique) => {
                    JSONObject meta = null;
                    if (unique) {
                        meta = new JSONObject(JSONObject.Type.Object);
                        meta.AddField("unique", true);
                    }
                    AwardItem(id, amt, meta);
                }));
                env.Global.Set("dp_profile_award_achievement", new Action<string, float>((id, p)  => AwardAchievement(id, p)));
                env.Global.Set("dp_profile_award_badge",       new Action<string>(id              => AwardBadge(id)));
                env.Global.Set("dp_profile_remove_item",       new Action<string, int>((id, amt) => RemoveItem(id, amt)));
                env.Global.Set("dp_profile_remove_badge",      new Action<string>(id             => RemoveBadge(id)));
                env.Global.Set("dp_profile_add_dreampoints",   new Action<int, string>((amt, reason) => AddDreamPoints(amt, reason)));
                env.Global.Set("dp_profile_spend_dreampoints", new Action<int, string>((amt, reason) => SpendDreamPoints(amt, reason)));
                // Playtime is reported automatically by SessionReporter via
                // /app/profile/session/heartbeat. We deliberately don't expose
                // it to Lua — creators reaching for that surface usually want
                // game-specific gating which Lua-side accumulation can't get
                // right (see SessionReporter for the per-content tracking).

                // Ergonomic `dp.profile.*` namespace on top of the flat bindings.
                env.DoString(@"
                    dp = dp or {}
                    dp.profile = {
                        isLoaded         = function() return dp_profile_is_loaded() end,
                        isBound          = function() return dp_profile_is_bound() end,
                        identity         = function() return dp_profile_identity() end,
                        contentId        = function() return dp_profile_content_id() end,
                        onReady          = function(cb) dp_profile_on_ready(cb) end,
                        refresh          = function() dp_profile_refresh() end,

                        getItem          = function(id)   return dp_profile_get_item(id) end,
                        getItemByType    = function(t)    return dp_profile_get_item_by_type(t) end,
                        getItemByName    = function(n)    return dp_profile_get_item_by_name(n) end,
                        hasItem          = function(id)   return dp_profile_has_item(id) end,
                        hasItemByType    = function(t)    return dp_profile_has_item_by_type(t) end,

                        getAchievement   = function(id)   return dp_profile_get_achievement(id) end,
                        hasAchievement   = function(id)   return dp_profile_has_achievement(id) end,

                        getBadge         = function(id)   return dp_profile_get_badge(id) end,
                        hasBadge         = function(id)   return dp_profile_has_badge(id) end,

                        getDreamPoints   = function()     return dp_profile_get_dreampoints() end,

                        awardItem        = function(id, amt, unique) dp_profile_award_item(id, amt or 1, unique or false) end,
                        awardAchievement = function(id, p)    dp_profile_award_achievement(id, p or 1) end,
                        awardBadge       = function(id)       dp_profile_award_badge(id) end,
                        removeItem       = function(id, amt)  dp_profile_remove_item(id, amt or 1) end,
                        removeBadge      = function(id)       dp_profile_remove_badge(id) end,
                        -- Production-only: server rejects from SDK preview builds. Wire it
                        -- into your game logic; mints succeed once shipped through Core.
                        addDreamPoints   = function(amt, reason) dp_profile_add_dreampoints(amt or 0, reason) end,
                        spendDreamPoints = function(amt, reason) dp_profile_spend_dreampoints(amt or 0, reason) end,
                    }
                ", "dp.profile.bootstrap");
            }
            catch (Exception e)
            {
                Debug.LogError("[ProfileAPI] Failed to register Lua bridge: " + e);
            }
        }

        // ── C# → LuaTable marshallers ────────────────────────────────────
        // Convert our domain objects into native Lua tables so Lua scripts
        // get plain values they can read with dot notation without XLua
        // having to generate wraps for ProfileItem/Achievement/Badge.

        static LuaTable ItemToLuaTable(XLua.LuaEnv env, ProfileItem it)
        {
            if (it == null) return null;
            var t = env.NewTable();
            t.Set("itemId",     it.itemId);
            t.Set("instanceId", it.instanceId);
            t.Set("baseItemId", it.baseItemId);
            t.Set("name",       it.name);
            t.Set("type",       it.type);
            t.Set("contentId",  it.contentId);
            t.Set("gameId",     it.gameId);
            t.Set("rarity",     it.rarity);
            t.Set("iconUri",    it.iconUri);
            t.Set("modelUri",   it.modelUri);
            t.Set("amount",     it.amount);
            // metadata: a free-form sub-table. Convert recursively from JSON.
            t.Set("metadata",   JsonObjectToLuaTable(env, it.metadata));
            return t;
        }

        static LuaTable AchievementToLuaTable(XLua.LuaEnv env, ProfileAchievement a)
        {
            if (a == null) return null;
            var t = env.NewTable();
            t.Set("achievementId", a.achievementId);
            t.Set("progress",      a.progress);
            t.Set("completed",     a.completed);
            t.Set("maxValue",      a.maxValue);
            t.Set("name",          a.name);
            t.Set("contentId",     a.contentId);
            return t;
        }

        static LuaTable BadgeToLuaTable(XLua.LuaEnv env, ProfileBadge b)
        {
            if (b == null) return null;
            var t = env.NewTable();
            t.Set("badgeId",     b.badgeId);
            t.Set("name",        b.name);
            t.Set("contentId",   b.contentId);
            t.Set("iconUri",     b.iconUri);
            t.Set("description", b.description);
            t.Set("awardedAtMs", b.awardedAtMs);
            return t;
        }

        // Recursive JSON → Lua table for metadata sub-objects. Returns nil
        // if json is null/missing; primitives boxed into typed values that
        // XLua can natively marshal.
        static LuaTable JsonObjectToLuaTable(XLua.LuaEnv env, JSONObject obj)
        {
            if (obj == null || obj.type == JSONObject.Type.Null) return null;
            var t = env.NewTable();
            if (obj.type == JSONObject.Type.Object)
            {
                for (int i = 0; i < obj.keys.Count; i++)
                {
                    var k = obj.keys[i];
                    var v = obj.list[i];
                    switch (v.type)
                    {
                        case JSONObject.Type.String: t.Set(k, v.stringValue); break;
                        case JSONObject.Type.Number: t.Set(k, v.floatValue); break;
                        case JSONObject.Type.Bool:   t.Set(k, v.boolValue); break;
                        case JSONObject.Type.Object:
                        case JSONObject.Type.Array:  t.Set(k, JsonObjectToLuaTable(env, v)); break;
                        case JSONObject.Type.Null:   break;
                    }
                }
            }
            else if (obj.type == JSONObject.Type.Array)
            {
                for (int i = 0; i < obj.list.Count; i++)
                {
                    var v = obj.list[i];
                    switch (v.type)
                    {
                        case JSONObject.Type.String: t.Set(i + 1, v.stringValue); break;
                        case JSONObject.Type.Number: t.Set(i + 1, v.floatValue); break;
                        case JSONObject.Type.Bool:   t.Set(i + 1, v.boolValue); break;
                        case JSONObject.Type.Object:
                        case JSONObject.Type.Array:  t.Set(i + 1, JsonObjectToLuaTable(env, v)); break;
                        case JSONObject.Type.Null:   break;
                    }
                }
            }
            return t;
        }
    }
}
